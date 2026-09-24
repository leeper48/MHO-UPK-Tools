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

    public static int Run(string folder, string tilePrefix, string libraryPackage, string outFbx, float minHeight, float minFootprint, float inset, string[] skip)
    {
        var tiles = Directory.EnumerateFiles(folder, tilePrefix + "*.upk").Where(f => !Program.IsBackupName(f)).OrderBy(f => f).ToList();
        if (tiles.Count == 0) { Console.WriteLine($"No packages matching {tilePrefix}*.upk"); return 1; }
        Console.WriteLine($"Reading {tiles.Count} tile package(s) matching {tilePrefix}*; shared meshes from {Path.GetFileName(libraryPackage)}");

        var library = Package.Open(libraryPackage);
        var libraryIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < library.Exports.Length; i++)
            if (library.ClassOf(library.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) libraryIndex.TryAdd(library.Exports[i].ObjectName, i);

        var packages = new Dictionary<string, Package> { ["lib"] = library };
        var placed = new List<Placed>();
        int unresolved = 0, hiddenCount = 0, skipped = 0;
        foreach (string tile in tiles)
        {
            var pkg = Package.Open(tile);
            string tileKey = Path.GetFileNameWithoutExtension(tile);
            packages[tileKey] = pkg;
            foreach (var e in pkg.Exports)
            {
                if (!pkg.ClassOf(e).Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
                var c = ComponentTransform.Read(pkg, pkg.ReadExportBytes(e));
                if (c is null || c.MeshRef == 0) continue;
                if (c.HiddenGame) { hiddenCount++; continue; }   // e.g. 256_modblock blocking volumes: invisible in game
                string meshName = pkg.RefName(c.MeshRef);
                if (skip.Any(k => meshName.Contains(k, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; }
                string key; Package owner; int index;
                if (c.MeshRef > 0) { key = $"{tileKey}:{c.MeshRef - 1}"; owner = pkg; index = c.MeshRef - 1; }
                else if (libraryIndex.TryGetValue(meshName, out int li)) { key = $"lib:{li}"; owner = library; index = li; }
                else { unresolved++; continue; }
                if (StaticMesh.ReadBounds(owner, owner.Exports[index]) is not { } bb) { unresolved++; continue; }
                placed.Add(new Placed(tileKey, meshName, key, c.Translation, c.Rotation, c.Scale, bb.Origin, bb.Extent));
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
        foreach (var p in buildings)
            if (shapes[p.ShapeKey] is { } shape) prisms.Add(Prism(p, shape, inset));

        int verts = prisms.Sum(x => x.Bottom.Length * 6), tris = prisms.Sum(x => x.Bottom.Length * 4 - 4);
        Console.WriteLine($"  {placed.Count:N0} visible placed meshes ({hiddenCount} HiddenGame, {skipped} matching skip list [{string.Join(", ", skip)}], {unresolved} unknown: skipped)");
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
    static Matrix4x4 RotatorMatrix(Vector3 units)
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
            Vector3 f = ToFileSpace(v), fn = ToFileSpace(normal);
            mesh.Vertices.Add(new Vector3D(f.X, f.Y, f.Z));
            mesh.Normals.Add(new Vector3D(fn.X, fn.Y, fn.Z));
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
    public static ComponentTransform? Read(Package pkg, byte[] d)
    {
        foreach (int start in new[] { 8, 4, 16 })
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
                        case "translation" when size == 12: t = V(d, p); break;
                        case "rotation" when size == 12: r = new Vector3(BitConverter.ToInt32(d, p), BitConverter.ToInt32(d, p + 4), BitConverter.ToInt32(d, p + 8)); break;
                        case "scale3d" when size == 12: s3 = V(d, p); break;
                        case "scale" when size == 4: s = BitConverter.ToSingle(d, p); break;
                        case "hiddengame" when type == "boolproperty": hidden = d[p - 1] != 0; break;
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
