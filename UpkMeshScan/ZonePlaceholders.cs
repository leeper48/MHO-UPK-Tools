using System.Numerics;
using Assimp;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace UpkMeshScan;

/// <summary>
/// --zone-placeholders: low-poly stand-ins for every building-sized placed mesh in a zone's map tiles.
/// For each StaticMeshComponent in the tiles (e.g. the 36 UES_Static_* Midtown free-roam tiles), its
/// Translation / Rotation / Scale3D (tiles store these in world coordinates — confirmed: cell medians sit on
/// a 2304-unit grid) place a prism built from the mesh's own footprint: the convex hull of its vertices seen
/// from above (so triangular buildings stay triangular), simplified to at most MaxCorners, shrunk toward its
/// centre by Inset and extruded from the mesh's bottom to Inset of its height — so the real building hides
/// it once its cell loads. Meshes come from the tile or from the region package the tile imports them from
/// (the live one, so earlier mesh mods are followed). HiddenGame components (blocking volumes) and names
/// matching the skip list (trees by default) are left out. One FBX at real size, same file space as
/// --export-fbx, for review in Blender. Read-only.
/// </summary>
static class ZonePlaceholders
{
    const int MaxCorners = 16;

    public sealed record Placed(string Tile, string Mesh, string ShapeKey, Vector3 Translation, Vector3 RotationUnits, Vector3 Scale, Vector3 BoundsOrigin, Vector3 BoundsExtent);

    /// <summary>Footprint outline (mesh-local XY, convex) and height range.</summary>
    sealed record Shape(List<Vector2> Hull, float MinZ, float MaxZ);

    public static int Run(string folder, string tilePrefix, string libraryPackage, string outFbx, float minHeight, float minFootprint, float inset, string[] skip, string[]? only = null,
        string? groundBoxes = null, float boxTop = -8f, float boxBottom = -220f, bool noMeshes = false,
        float raster = 0f, float rasterMinZ = 40f, float rasterStep = 32f)
    {
        // Tiles: packages matching the prefix (placements already in world coordinates, e.g. Midtown), or a layout
        // file (.txt: "cell <TAB> x <TAB> y" per line, # comments) for zones laid out by a generator, whose cell
        // packages are stored centred on the origin: each cell's placements are moved to its logged centre (the
        // cellpos of MHServerEmu's region generation log, e.g. Industry City).
        var tiles = new List<(string Path, string Label, Vector3 Offset)>();
        if (tilePrefix.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && File.Exists(tilePrefix))
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (string line in File.ReadLines(tilePrefix))
            {
                if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0) continue;
                string[] f = line.Split('\t');
                string path = Path.Combine(folder, f[0].Trim() + ".upk");
                if (!File.Exists(path)) { Console.WriteLine($"Layout cell {f[0]}: no package {path}"); return 1; }
                var offset = new Vector3(float.Parse(f[1], inv), float.Parse(f[2], inv), f.Length > 3 ? float.Parse(f[3], inv) : 0f);
                tiles.Add((path, $"{f[0].Trim()}@{offset.X:0},{offset.Y:0}", offset));
            }
            Console.WriteLine($"Reading {tiles.Count} cell(s) from layout {Path.GetFileName(tilePrefix)} ({tiles.Select(t => t.Path).Distinct().Count()} packages); shared meshes from {Path.GetFileName(libraryPackage)}");
        }
        else
        {
            tiles = Directory.EnumerateFiles(folder, tilePrefix + "*.upk").Where(f => !Program.IsBackupName(f)).OrderBy(f => f)
                .Select(f => (f, Path.GetFileNameWithoutExtension(f), Vector3.Zero)).ToList();
            if (tiles.Count == 0) { Console.WriteLine($"No packages matching {tilePrefix}*.upk"); return 1; }
            Console.WriteLine($"Reading {tiles.Count} tile package(s) matching {tilePrefix}*; shared meshes from {Path.GetFileName(libraryPackage)}");
        }

        var library = Package.Open(libraryPackage);
        var libraryIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < library.Exports.Length; i++)
            if (library.ClassOf(library.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) libraryIndex.TryAdd(library.Exports[i].ObjectName, i);

        var packages = new Dictionary<string, Package> { ["lib"] = library };
        var placed = new List<Placed>();
        int unresolved = 0, hiddenCount = 0, skipped = 0, actorPlaced = 0;
        foreach (var (tile, label, offset) in tiles)
        {
            string tileKey = Path.GetFileNameWithoutExtension(tile);
            if (!packages.TryGetValue(tileKey, out var pkg)) packages[tileKey] = pkg = Package.Open(tile);
            foreach (var e in pkg.Exports)
            {
                if (!pkg.ClassOf(e).Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
                var c = ComponentTransform.Read(pkg, pkg.ReadExportBytes(e));
                if (c is null || c.MeshRef == 0) continue;
                if (c.HiddenGame) { hiddenCount++; continue; }   // e.g. 256_modblock blocking volumes: invisible in game
                string meshName = pkg.RefName(c.MeshRef);
                if (skip.Any(k => meshName.Contains(k, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                if (only is { Length: > 0 } && !only.Any(k => meshName.Equals(k, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                string key; Package owner; int index;
                if (c.MeshRef > 0) { key = $"{tileKey}:{c.MeshRef - 1}"; owner = pkg; index = c.MeshRef - 1; }
                else if (libraryIndex.TryGetValue(meshName, out int li)) { key = $"lib:{li}"; owner = library; index = li; }
                else { unresolved++; continue; }
                if (StaticMesh.ReadBounds(owner, owner.Exports[index]) is not { } bb) { unresolved++; continue; }
                // A component owned by a standalone actor (StaticMeshActor, InterpActor, ...) is placed by that actor's
                // Location / Rotation / DrawScale; collection-actor components carry their own transform. (Asgard's
                // Bifrost gun: InterpActor at 1920,-6016, yaw 180, component without a transform; missed before 2.16.1.)
                Vector3 t = c.Translation, r = c.Rotation, sc = c.Scale;
                if (e.OuterIndex > 0 && pkg.ClassOf(pkg.Exports[e.OuterIndex - 1]) is string oc && oc.EndsWith("Actor", StringComparison.OrdinalIgnoreCase)
                    && !oc.Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)
                    && ComponentTransform.ReadActor(pkg, pkg.ReadExportBytes(pkg.Exports[e.OuterIndex - 1])) is { } a)
                {
                    if (a.HiddenGame) { hiddenCount++; continue; }
                    t = a.Translation + Vector3.Transform(t * a.Scale, RotatorMatrix(a.Rotation));
                    r = a.Rotation + r;                         // exact for yaw-only turns (the usual case); pitch/roll only approximate
                    sc = a.Scale * sc;
                    actorPlaced++;
                }
                placed.Add(new Placed(label, meshName, key, t + offset, r, sc, bb.Origin, bb.Extent));
            }
        }

        // Building-sized only (cheap check on stored bounds): tall enough, and the longest horizontal side long
        // enough, so thin modular wall pieces still count.
        var buildings = placed.Where(p =>
        {
            Vector3 size = p.BoundsExtent * 2 * Vector3.Abs(p.Scale);
            return size.Z >= minHeight && MathF.Max(size.X, size.Y) >= minFootprint;
        }).ToList();

        // Footprint of each distinct building mesh, decoded once.
        var shapes = new Dictionary<string, Shape?>();
        foreach (var p in buildings)
        {
            if (shapes.ContainsKey(p.ShapeKey)) continue;
            string[] k = p.ShapeKey.Split(':');
            var owner = packages[k[0]];
            try { shapes[p.ShapeKey] = Footprint(StaticMesh.Read(owner, owner.Exports[int.Parse(k[1])]).Positions); }
            catch (PackageFormatException) { shapes[p.ShapeKey] = null; }
        }

        var prisms = new List<(Placed P, Vector3[] Bottom, Vector3[] Top)>();
        if (raster > 0 && !noMeshes)
        {
            prisms.AddRange(Raster(placed, packages, raster, rasterMinZ, rasterStep, minHeight, minFootprint));
            noMeshes = true;                                    // the raster replaces the per-mesh prisms
        }
        if (!noMeshes)
            foreach (var p in buildings)
                if (shapes[p.ShapeKey] is { } shape) prisms.Add(Prism(p, shape, inset));
        if (groundBoxes != null)
        {
            // Ground slabs: "x0 y0 x1 y1 [surface z]" rectangles in world units (e.g. from the cells' height maps), top just under
            // the real ground, sides down to the water, so pier edges read as solid. Not inset: the real ground covers them.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int n = 0;
            foreach (string line in File.ReadLines(groundBoxes))
            {
                if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0) continue;
                float[] r = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, inv)).ToArray();
                Vector2[] corners = [new(r[0], r[1]), new(r[2], r[1]), new(r[2], r[3]), new(r[0], r[3])];
                var ground = new Placed("ground", "ground_slab", "", Vector3.Zero, Vector3.Zero, Vector3.One, Vector3.Zero, Vector3.Zero);
                // Optional 5th value: this slab's own surface height (multi-level zones, e.g. Odin's Palace): the slab
                // keeps the same top offset and thickness relative to it (boxTop / boxBottom are then relative to 0).
                float lift = r.Length >= 5 ? r[4] : 0f;
                prisms.Add((ground, corners.Select(c => new Vector3(c, boxBottom + lift)).ToArray(), corners.Select(c => new Vector3(c, boxTop + lift)).ToArray()));
                n++;
            }
            Console.WriteLine($"  ground: {n} slab(s) from {Path.GetFileName(groundBoxes)}, top z {boxTop}, bottom z {boxBottom}{(noMeshes ? "; meshes left out (--no-meshes)" : "")}");
        }

        int verts = prisms.Sum(x => x.Bottom.Length * 6), tris = prisms.Sum(x => x.Bottom.Length * 4 - 4);
        Console.WriteLine($"  {placed.Count:N0} visible placed meshes ({actorPlaced} placed by their owning actor; {hiddenCount} HiddenGame, {skipped} matching skip list [{string.Join(", ", skip)}], {unresolved} unknown: skipped)");
        Console.WriteLine($"  {prisms.Count:N0} building-sized (height >= {minHeight}, longest side >= {minFootprint}) -> footprint prisms at {inset:P0}, <= {MaxCorners} corners each; {verts:N0} vertices, {tris:N0} triangles");
        foreach (var g in prisms.GroupBy(b => b.P.Mesh).OrderByDescending(g => g.Count()).Take(10))
            Console.WriteLine($"    {g.Count(),4} x {g.Key}  ({g.First().Bottom.Length} corners)");
        if (prisms.Count == 0) return 1;

        var all = prisms.SelectMany(b => b.Bottom.Concat(b.Top)).ToList();
        Vector3 min = all.Aggregate(Vector3.Min), max = all.Aggregate(Vector3.Max);
        Console.WriteLine($"  world bounds: ({min.X:0}, {min.Y:0}, {min.Z:0}) .. ({max.X:0}, {max.Y:0}, {max.Z:0})");

        Write(prisms.Select(b => (b.Bottom, b.Top)).ToList(), outFbx);
        var lines = new List<string> { "tile\tmesh\tx\ty\tz\tyaw_deg\tscale\tcorners" };
        foreach (var b in prisms)
            lines.Add($"{b.P.Tile}\t{b.P.Mesh}\t{b.P.Translation.X:0}\t{b.P.Translation.Y:0}\t{b.P.Translation.Z:0}\t{b.P.RotationUnits.Y * 360f / 65536f:0.#}\t{b.P.Scale.X:0.##},{b.P.Scale.Y:0.##},{b.P.Scale.Z:0.##}\t{b.Bottom.Length}");
        File.WriteAllLines(Path.ChangeExtension(outFbx, ".txt"), lines);
        Console.WriteLine($"Wrote {outFbx} (+ .txt list)");
        return 0;
    }

    /// <summary>
    /// --raster: the placed meshes' real triangles seen from above, on a grid of `cell` units. Each grid square keeps
    /// the highest point of any triangle covering its centre, or of any edge crossing it (so thin walls count).
    /// Squares at least minZ high are rounded DOWN to `step` (the box stays inside the real top) and merged into
    /// rectangles of equal height, never across a 2304-unit cell boundary; each becomes a box from z 0 to its
    /// height, pulled in by 1 unit so no two share a corner. Follows real outlines (L-shaped buildings, courtyards,
    /// roofs, ship hulls, container stacks) where one convex prism per mesh can't. Only meshes that pass the size
    /// filter (height, longest side) are rasterised.
    /// </summary>
    static List<(Placed, Vector3[], Vector3[])> Raster(List<Placed> placed, Dictionary<string, Package> packages, float cell, float minZ, float step, float minHeight, float minFootprint)
    {
        var top = new Dictionary<(int, int), float>();
        var meshes = new Dictionary<string, StaticMesh?>();
        int used = 0;
        void Put(float x, float y, float z)
        {
            var k = ((int)MathF.Floor(x / cell), (int)MathF.Floor(y / cell));
            if (!top.TryGetValue(k, out float t) || z > t) top[k] = z;
        }
        foreach (var p in placed)
        {
            Vector3 size = p.BoundsExtent * 2 * Vector3.Abs(p.Scale);
            if (size.Z < minHeight || MathF.Max(size.X, size.Y) < minFootprint) continue;
            if (!meshes.TryGetValue(p.ShapeKey, out var m))
            {
                string[] k = p.ShapeKey.Split(':');
                try { m = StaticMesh.Read(packages[k[0]], packages[k[0]].Exports[int.Parse(k[1])]); } catch (PackageFormatException) { m = null; }
                meshes[p.ShapeKey] = m;
            }
            if (m == null) continue;
            used++;
            Matrix4x4 rot = RotatorMatrix(p.RotationUnits);
            var w = m.Positions.Select(v => Vector3.Transform(v * p.Scale, rot) + p.Translation).ToArray();
            for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            {
                Vector3 a = w[m.Indices[t]], b = w[m.Indices[t + 1]], c = w[m.Indices[t + 2]];
                // Edges: sample every half grid square.
                foreach (var (e0, e1) in new[] { (a, b), (b, c), (c, a) })
                {
                    float len = Vector2.Distance(new(e0.X, e0.Y), new(e1.X, e1.Y));
                    int n = Math.Max(1, (int)MathF.Ceiling(len / (cell / 2)));
                    for (int i = 0; i <= n; i++) { var q = Vector3.Lerp(e0, e1, i / (float)n); Put(q.X, q.Y, q.Z); }
                }
                // Faces: grid-square centres inside the triangle (top view), height interpolated.
                float det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(det) < 1e-3f) continue;
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / cell), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / cell);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / cell), y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / cell);
                for (int gx = x0; gx <= x1; gx++)
                    for (int gy = y0; gy <= y1; gy++)
                    {
                        float px = (gx + 0.5f) * cell, py = (gy + 0.5f) * cell;
                        float l1 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / det;
                        float l2 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / det;
                        float l3 = 1 - l1 - l2;
                        if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                        float z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                        var k = (gx, gy);
                        if (!top.TryGetValue(k, out float old) || z > old) top[k] = z;
                    }
            }
        }
        // Heights rounded down to the step; squares below minZ dropped.
        var level = top.Where(kv => kv.Value >= minZ).ToDictionary(kv => kv.Key, kv => MathF.Floor(kv.Value / step) * step);
        int perCell = (int)MathF.Round(2304f / cell);
        (int, int) CellOf((int X, int Y) k) => ((int)MathF.Floor(k.X / (float)perCell), (int)MathF.Floor(k.Y / (float)perCell));
        var done = new HashSet<(int, int)>();
        var boxes = new List<(Placed, Vector3[], Vector3[])>();
        var marker = new Placed("raster", "raster_box", "", Vector3.Zero, Vector3.Zero, Vector3.One, Vector3.Zero, Vector3.Zero);
        bool Ok((int, int) k, float h, (int, int) ci) => !done.Contains(k) && level.TryGetValue(k, out float v) && v == h && CellOf(k) == ci;
        foreach (var k in level.Keys.OrderBy(k => k.Item2).ThenBy(k => k.Item1))
        {
            if (done.Contains(k)) continue;
            float h = level[k]; var ci = CellOf(k);
            int wdt = 1;
            while (Ok((k.Item1 + wdt, k.Item2), h, ci)) wdt++;
            int hgt = 1;
            while (Enumerable.Range(0, wdt).All(i => Ok((k.Item1 + i, k.Item2 + hgt), h, ci))) hgt++;
            for (int i = 0; i < wdt; i++) for (int j = 0; j < hgt; j++) done.Add((k.Item1 + i, k.Item2 + j));
            float ax = k.Item1 * cell + 1, ay = k.Item2 * cell + 1, bx = (k.Item1 + wdt) * cell - 1, by = (k.Item2 + hgt) * cell - 1;
            Vector2[] c = [new(ax, ay), new(bx, ay), new(bx, by), new(ax, by)];
            boxes.Add((marker, c.Select(v => new Vector3(v, 0)).ToArray(), c.Select(v => new Vector3(v, h)).ToArray()));
        }
        Console.WriteLine($"  raster: {used:N0} meshes on a {cell}-unit grid -> {level.Count:N0} squares >= {minZ} high (heights rounded down to {step}) -> {boxes.Count:N0} boxes");
        return boxes;
    }

    /// <summary>Convex hull of the vertices seen from above, simplified to MaxCorners by dropping the least important corner.</summary>
    static Shape? Footprint(Vector3[] positions)
    {
        if (positions.Length < 3) return null;
        var pts = positions.Select(v => new Vector2(v.X, v.Y)).Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (pts.Count < 3) return null;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Vector2>();
        foreach (var pass in new[] { pts, Enumerable.Reverse(pts).ToList() })   // Andrew's monotone chain
        {
            int start = hull.Count;
            foreach (var p in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        if (hull.Count < 3) return null;
        // Visvalingam: removing a corner of a convex polygon keeps it convex and only shrinks it (stays inside).
        while (hull.Count > MaxCorners)
        {
            int worst = 0; float smallest = float.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                float area = MathF.Abs(Cross(hull[(i + hull.Count - 1) % hull.Count], hull[i], hull[(i + 1) % hull.Count]));
                if (area < smallest) { smallest = area; worst = i; }
            }
            hull.RemoveAt(worst);
        }
        return new Shape(hull, positions.Min(v => v.Z), positions.Max(v => v.Z));
    }

    /// <summary>Outline shrunk toward its centre by inset, extruded from the mesh bottom to inset of its height, then placed.</summary>
    static (Placed, Vector3[], Vector3[]) Prism(Placed p, Shape s, float inset)
    {
        Matrix4x4 rot = RotatorMatrix(p.RotationUnits);
        Vector2 centre = s.Hull.Aggregate(Vector2.Zero, (a, v) => a + v) / s.Hull.Count;
        float top = s.MinZ + (s.MaxZ - s.MinZ) * inset;
        Vector3 Place(Vector2 xy, float z) => Vector3.Transform(new Vector3(centre + (xy - centre) * inset, z) * p.Scale, rot) + p.Translation;
        return (p, s.Hull.Select(v => Place(v, s.MinZ)).ToArray(), s.Hull.Select(v => Place(v, top)).ToArray());
    }

    /// <summary>UE3 FRotationMatrix for a rotator in 65536-per-turn units (X = pitch, Y = yaw, Z = roll), row vectors.</summary>
    internal static Matrix4x4 RotatorMatrix(Vector3 units)
    {
        float k = MathF.PI * 2f / 65536f;
        float sp = MathF.Sin(units.X * k), cp = MathF.Cos(units.X * k);
        float sy = MathF.Sin(units.Y * k), cy = MathF.Cos(units.Y * k);
        float sr = MathF.Sin(units.Z * k), cr = MathF.Cos(units.Z * k);
        return new Matrix4x4(
            cp * cy, cp * sy, sp, 0,
            sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp, 0,
            -(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp, 0,
            0, 0, 0, 1);
    }

    static void Write(List<(Vector3[] Bottom, Vector3[] Top)> prisms, string path)
    {
        var scene = new Scene { RootNode = new Node("zone_placeholders") };
        var mesh = new Mesh("placeholders", PrimitiveType.Triangle);
        foreach (var (bottom, top) in prisms)
        {
            Vector3 centre = bottom.Concat(top).Aggregate(Vector3.Zero, (a, v) => a + v) / (bottom.Length * 2);
            int n = bottom.Length;
            AddFace(mesh, bottom, centre);
            AddFace(mesh, top, centre);
            for (int i = 0; i < n; i++)
                AddFace(mesh, [bottom[i], bottom[(i + 1) % n], top[(i + 1) % n], top[i]], centre);
        }
        scene.Materials.Add(new Material { Name = "placeholder_gray" });
        mesh.MaterialIndex = 0;
        scene.Meshes.Add(mesh);
        scene.RootNode.MeshIndices.Add(0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var ctx = new AssimpContext();
        if (!ctx.ExportFile(scene, path, "fbx")) throw new IOException($"Assimp could not write {path}");
    }

    /// <summary>A flat convex face (fan-triangulated, own vertices so it shades flat), facing away from the prism centre.</summary>
    static void AddFace(Mesh mesh, Vector3[] poly, Vector3 centre)
    {
        Vector3 normal = Vector3.Zero;                          // Newell's method: robust for any convex polygon
        for (int i = 0; i < poly.Length; i++)
        {
            Vector3 a = poly[i], b = poly[(i + 1) % poly.Length];
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        if (normal.LengthSquared() < 1e-12f) return;
        normal = Vector3.Normalize(normal);
        Vector3 faceCentre = poly.Aggregate(Vector3.Zero, (a, v) => a + v) / poly.Length;
        bool flip = Vector3.Dot(normal, faceCentre - centre) < 0;
        if (flip) normal = -normal;
        int baseIndex = mesh.VertexCount;
        foreach (var v in poly)
        {
            Vector3 f = ToFileSpace(v);
            mesh.Vertices.Add(new Vector3D(f.X, f.Y, f.Z));
        }
        // Engine winding: on stock meshes cross(v1-v0, v2-v0) points AGAINST the normal (all 21,960 triangles of
        // nyc_midtown_bldg_b_buildinga_a). Newell's normal follows the polygon order, so emit the fan reversed
        // when that order faces outward. --export-fbx's Y/Z swap then makes it face outward in Blender.
        for (int i = 1; i + 1 < poly.Length; i++)
            mesh.Faces.Add(flip ? new Face([baseIndex, baseIndex + i, baseIndex + i + 1]) : new Face([baseIndex, baseIndex + i + 1, baseIndex + i]));
    }

    static Vector3 ToFileSpace(Vector3 v) => new(v.X, v.Z, v.Y);
}

/// <summary>StaticMesh reference and placement of a StaticMeshComponent (MHO layout: properties from byte 8).</summary>
public sealed record ComponentTransform(int MeshRef, Vector3 Translation, Vector3 Rotation, Vector3 Scale, bool HiddenGame = false)
{
    public static ComponentTransform? Read(Package pkg, byte[] d) => Read(pkg, d, [8, 4, 16]);

    /// <summary>
    /// An actor's placement (Location, Rotation, DrawScale, DrawScale3D, bHidden) as a transform. Actor exports have a
    /// few native fields before their tags (e.g. Asgardia_Bridge_EXT_INS_B's InterpActor: tags from byte 0x1A), so every
    /// start up to 96 is tried and the first that walks cleanly to None is used.
    /// </summary>
    public static ComponentTransform? ReadActor(Package pkg, byte[] d) => Read(pkg, d, Enumerable.Range(4, 93));

    static ComponentTransform? Read(Package pkg, byte[] d, IEnumerable<int> starts)
    {
        foreach (int start in starts)
        {
            int p = start, mesh = 0;
            bool hidden = false;
            Vector3 t = Vector3.Zero, r = Vector3.Zero, s3 = Vector3.One;
            float s = 1f;
            try
            {
                for (int guard = 0; guard < 1024; guard++)
                {
                    string name = Name(pkg, d, ref p);
                    if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return new ComponentTransform(mesh, t, r, s3 * s, hidden);
                    string type = Name(pkg, d, ref p).ToLowerInvariant();
                    int size = BitConverter.ToInt32(d, p); p += 8;
                    if (type is "structproperty" or "byteproperty") Name(pkg, d, ref p);
                    if (type == "boolproperty") p += 1;
                    if (size < 0 || p + size > d.Length) break;
                    switch (name.ToLowerInvariant())
                    {
                        case "staticmesh" when size == 4: mesh = BitConverter.ToInt32(d, p); break;
                        case "translation" or "location" when size == 12: t = V(d, p); break;
                        case "rotation" when size == 12: r = new Vector3(BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4), BitConverter.ToInt32(d, p + 8)); break;
                        case "scale3d" or "drawscale3d" when size == 12: s3 = V(d, p); break;
                        case "scale" or "drawscale" when size == 4: s = BitConverter.ToSingle(d, p); break;
                        case "hiddengame" or "bhidden" when type == "boolproperty": hidden = d[p - 1] != 0; break;
                    }
                    p += size;
                }
            }
            catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException or ArgumentException) { }
        }
        return null;
    }

    static Vector3 V(byte[] d, int p) => new(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8));

    static string Name(Package pkg, byte[] d, ref int p)
    {
        if (p + 8 > d.Length) throw new PackageFormatException("past end");
        int idx = BitConverter.ToInt32(d, p), num = BitConverter.ToInt32(d, p + 4); p += 8;
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException("bad name");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }
}
