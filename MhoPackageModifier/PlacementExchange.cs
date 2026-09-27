using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Assimp;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace MhoPackageModifier;

/// <summary>
/// --export-placements / --import-placements: a round trip for adding placements (e.g. the missing back walls of
/// Hightown's buildings) by duplicating existing pieces in Blender.
///
/// Export: every kept placed mesh of the zone's tiles as its own FBX object, named T&lt;tile&gt;_E&lt;export&gt;_&lt;mesh&gt; (short:
/// Blender cuts names at 63), with its placement as the object transform and the mesh data shared between placements
/// of the same mesh; a sidecar &lt;out&gt;_placements.txt maps each id to its tile file, component path and game transform.
/// Filters as --export-placed (--min-height, --min-footprint, --skip, --skip-material, --offset).
///
/// Import: objects named like an export id plus a Blender duplicate suffix (".001") become new placements: a copy of
/// the original's component (same mesh, materials, flags) with the duplicate's position / rotation / scale, empty
/// lighting and no VisibilityId, added to the tile's StaticMeshCollectionActor. The file-to-game mapping (Blender's axis
/// and unit conversion) is fitted from the untouched originals' positions, so the export settings don't matter:
/// game(dup) = G(orig) * T * (W(orig)^-1 * W(dup)) * T^-1, W = the file's world matrices, T = the fitted file->game map.
/// One verified write per tile (one undo step each); --dry-run writes nothing.
/// </summary>
static class PlacementExchange
{
    static readonly byte[] EmptyLighting = [1, 0, 0, 0, .. new byte[17]];
    static readonly Matrix4x4 Swap = new(1, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1);   // game (x,y,z) <-> FBX (x,z,y)

    static Matrix4x4 GameMatrix(Vector3 t, Vector3 rot, Vector3 sc) =>
        Matrix4x4.CreateScale(sc) * ZonePlaceholders.RotatorMatrix(rot) * Matrix4x4.CreateTranslation(t);

    static Assimp.Matrix4x4 ToAssimp(Matrix4x4 m) => new(m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43, m.M14, m.M24, m.M34, m.M44);
    static Matrix4x4 FromAssimp(Assimp.Matrix4x4 a) => new(a.A1, a.B1, a.C1, a.D1, a.A2, a.B2, a.C2, a.D2, a.A3, a.B3, a.C3, a.D3, a.A4, a.B4, a.C4, a.D4);

    static string Fmt(Matrix4x4 m) => string.Join(",", new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
    static Matrix4x4 ParseM(string s) { var v = s.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray(); return new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]); }

    // ---------------------------------------------------------------- export

    public static int Export(string folder, string layout, string libraryPath, string outFbx, Vector3 offset, float minFootprint, float minHeight, string[] skip, string[] skipMaterials)
    {
        var inv = CultureInfo.InvariantCulture;
        var files = Directory.EnumerateFiles(folder, "*.upk").Where(f => !Program.IsBackupName(f)).ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
        var tiles = new List<string>();
        foreach (string line in File.ReadLines(layout))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            string[] cols = line.Split('\t');
            string name = cols[0];
            // Tiled zones store placements in world coordinates, one file per place (layout positions 0). A layout with
            // positions (cells placed at run time, e.g. Industry City) reuses one cell file in several places: an edit
            // there would change every copy, and the export would put them all at the origin.
            if (cols.Length >= 3 && (float.Parse(cols[1], inv) != 0 || float.Parse(cols[2], inv) != 0))
            { Console.WriteLine($"  {Path.GetFileName(layout)} places cells at run time ({name} at {cols[1]}, {cols[2]}); the placement round trip only works for tiled zones. Use --export-placed to export the scene."); return 2; }
            if (files.TryGetValue(name, out string? p)) tiles.Add(p); else Console.WriteLine($"  tile {name} not found, skipped");
        }
        var library = Package.Open(libraryPath);
        var libByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var libByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < library.Exports.Length; i++)
        {
            libByPath.TryAdd(library.PathOf(library.Exports[i]), i);
            if (library.ClassOf(library.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) libByName.TryAdd(library.Exports[i].ObjectName, i);
        }
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outFbx))!;
        string texFolder = Path.GetFileNameWithoutExtension(outFbx) + "_textures";
        Directory.CreateDirectory(Path.Combine(outDir, texFolder));
        string cacheFolder = Path.GetDirectoryName(Path.GetFullPath(libraryPath))!;
        Console.WriteLine($"Placement export: {tiles.Count} tile(s), library {Path.GetFileName(libraryPath)}, offset {offset}");

        var scene = new Scene { RootNode = new Node(Path.GetFileNameWithoutExtension(outFbx)) };
        var materialIndex = new Dictionary<string, int>();
        var meshIndex = new Dictionary<string, List<int>>();                 // mesh key -> its Assimp meshes (one per kept section)
        var texWritten = new Dictionary<string, string>();
        var sidecar = new List<string> { "# id<TAB>tile file<TAB>component path<TAB>game world matrix (row-major, v' = v*M)",
            $"# offset {offset.X.ToString(CultureInfo.InvariantCulture)},{offset.Y.ToString(CultureInfo.InvariantCulture)},{offset.Z.ToString(CultureInfo.InvariantCulture)}",
            $"# library {Path.GetFileName(libraryPath)}" };
        int kept = 0;

        (Package Pkg, int Index, string Path)? Material(Package p, int r)
        {
            if (r > 0) return (p, r - 1, p.PathOf(p.Exports[r - 1]));
            if (r < 0)
            {
                var parts = new List<string>();
                for (int guard = 0; r < 0 && guard < 16; guard++) { var im = p.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
                string path = string.Join('.', parts);
                return libByPath.TryGetValue(path, out int li) ? (library, li, path) : null;
            }
            return null;
        }
        int MaterialSlot((Package Pkg, int Index, string Path)? mat)
        {
            string key = mat?.Path ?? "(no material)";
            if (materialIndex.TryGetValue(key, out int mi)) return mi;
            var am = new Assimp.Material { Name = TextureExport.SafeName(key) };
            if (mat is { } mm)
            {
                var notes = new List<string>();
                foreach (var tx in TextureExport.MaterialTextures(mm.Pkg, mm.Index + 1, notes))
                {
                    if (!tx.Parameter.Contains("diffuse", StringComparison.OrdinalIgnoreCase)) continue;
                    string texKey = $"{mm.Pkg.GetHashCode()}:{tx.ExportIndex}";
                    if (!texWritten.TryGetValue(texKey, out string? rel))
                    {
                        rel = Path.Combine(texFolder, TextureExport.SafeName(tx.Texture) + ".dds");
                        if (TextureExport.WriteDds(mm.Pkg, tx.ExportIndex, Path.Combine(outDir, rel), out _, cacheFolder) is null) rel = "";
                        texWritten[texKey] = rel;
                    }
                    if (rel.Length > 0) am.AddMaterialTexture(new TextureSlot(rel.Replace('\\', '/'), TextureType.Diffuse, 0, TextureMapping.FromUV, 0, 1f, TextureOperation.Add, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0));
                    break;
                }
            }
            scene.Materials.Add(am);
            return materialIndex[key] = scene.MaterialCount - 1;
        }

        for (int ti = 0; ti < tiles.Count; ti++)
        {
            var pkg = Package.Open(tiles[ti]);
            for (int ei = 0; ei < pkg.Exports.Length; ei++)
            {
                var e = pkg.Exports[ei];
                if (!pkg.ClassOf(e).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] d = pkg.ReadExportBytes(e);
                var c = ComponentTransform.Read(pkg, d);
                if (c == null || c.MeshRef == 0 || c.HiddenGame) continue;
                string meshName = pkg.RefName(c.MeshRef);
                if (skip.Any(k => meshName.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                Vector3 t = c.Translation, rot = c.Rotation, sc = c.Scale;
                if (e.OuterIndex > 0 && pkg.ClassOf(pkg.Exports[e.OuterIndex - 1]) is string oc && oc.EndsWith("Actor", StringComparison.OrdinalIgnoreCase)
                    && !oc.Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)
                    && ComponentTransform.ReadActor(pkg, pkg.ReadExportBytes(pkg.Exports[e.OuterIndex - 1])) is { } a)
                {
                    if (a.HiddenGame) continue;
                    t = a.Translation + Vector3.Transform(t * a.Scale, ZonePlaceholders.RotatorMatrix(a.Rotation));
                    rot = a.Rotation + rot; sc = a.Scale * sc;
                }
                Package owner; int index;
                if (c.MeshRef > 0) { owner = pkg; index = c.MeshRef - 1; }
                else if (libByName.TryGetValue(meshName, out int li)) { owner = library; index = li; }
                else continue;
                StaticMesh mesh;
                try { mesh = StaticMesh.Read(owner, owner.Exports[index]); } catch (PackageFormatException) { continue; }
                var g = GameMatrix(t + offset, rot, sc);
                var w = mesh.Positions.Select(v => Vector3.Transform(v, g)).ToArray();
                Vector3 lo = w.Aggregate(Vector3.Min), hi = w.Aggregate(Vector3.Max);
                if (MathF.Max(hi.X - lo.X, hi.Y - lo.Y) < minFootprint || hi.Z - lo.Z < minHeight) continue;

                // Component materials by section, else the mesh section's own; sections with skipped materials dropped.
                var tags = TagWalker.Walk(pkg, d, 8);
                var mt = tags?.FirstOrDefault(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
                var compMats = new List<int>();
                if (mt != null) { int n = BitConverter.ToInt32(d, mt.ValueAt); for (int k = 0; k < n; k++) compMats.Add(BitConverter.ToInt32(d, mt.ValueAt + 4 + 4 * k)); }
                var mats = new List<(Package, int, string)?>();
                for (int s = 0; s < mesh.Sections.Length; s++)
                    mats.Add(s < compMats.Count && compMats[s] != 0 ? Material(pkg, compMats[s]) : mesh.Sections[s].MaterialRef != 0 ? Material(owner, mesh.Sections[s].MaterialRef) : null);
                string key = $"{(owner == library ? "lib" : tiles[ti])}:{index}:" + string.Join("|", mats.Select(m => m?.Item3 ?? "-"));
                if (!meshIndex.TryGetValue(key, out var mlist))
                {
                    mlist = [];
                    for (int s = 0; s < mesh.Sections.Length; s++)
                    {
                        var sec = mesh.Sections[s];
                        string mp = mats[s]?.Item3 ?? "(no material)";
                        if (sec.NumTriangles <= 0 || skipMaterials.Any(k => mp.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                        var am = new Mesh(TextureExport.SafeName(meshName) + $"_s{s}", PrimitiveType.Triangle) { MaterialIndex = MaterialSlot(mats[s]) };
                        am.UVComponentCount[0] = 2;
                        var used = new Dictionary<int, int>();
                        int end = sec.FirstIndex + sec.NumTriangles * 3;
                        for (int i = sec.FirstIndex; i < end; i++)
                        {
                            int v = mesh.Indices[i];
                            if (used.ContainsKey(v)) continue;
                            used[v] = am.VertexCount;
                            var p = mesh.Positions[v];
                            am.Vertices.Add(new Vector3D(p.X, p.Z, p.Y));                        // local, FBX axes
                            var uv = mesh.TexCoords[0][v];
                            am.TextureCoordinateChannels[0].Add(new Vector3D(uv.X, 1f - uv.Y, 0f));
                        }
                        // The swap flips handedness, as in --export-placed: keep the engine winding order.
                        for (int i = sec.FirstIndex; i + 2 < end; i += 3)
                            am.Faces.Add(new Face([used[mesh.Indices[i]], used[mesh.Indices[i + 1]], used[mesh.Indices[i + 2]]]));
                        scene.Meshes.Add(am);
                        mlist.Add(scene.MeshCount - 1);
                    }
                    meshIndex[key] = mlist;
                }
                if (mlist.Count == 0) continue;
                string id = $"T{ti}_E{ei + 1}_{meshName}";
                if (id.Length > 55) id = id[..55];                                          // Blender: 63 incl. ".001"
                var node = new Node(id, scene.RootNode) { Transform = ToAssimp(Swap * g * Swap) };
                node.MeshIndices.AddRange(mlist);
                scene.RootNode.Children.Add(node);
                sidecar.Add($"{id}\t{Path.GetFileName(tiles[ti])}\t{pkg.PathOf(e)}\t{Fmt(g)}");
                kept++;
            }
        }
        using var ctx = new AssimpContext();
        if (!ctx.ExportFile(scene, outFbx, "fbx")) { Console.WriteLine("  FBX export failed"); return 1; }
        string side = Path.Combine(outDir, Path.GetFileNameWithoutExtension(outFbx) + "_placements.txt");
        File.WriteAllLines(side, sidecar);
        Console.WriteLine($"  {kept:N0} placements, {scene.MeshCount:N0} shared meshes, {scene.MaterialCount} materials -> {outFbx}");
        Console.WriteLine($"  ids -> {side}");
        return 0;
    }

    /// <summary>
    /// --test-placements: from the exported FBX, a test file with known edits, run through --import-placements (dry run):
    /// the first placement duplicated (+500 x, +90 yaw, same geometry), the second moved by +300 y, and the first
    /// duplicated again at +1000 y with its geometry scaled 1.5 in its own space (a mesh edit: a new mesh in the tile).
    /// Written as &lt;fbx&gt;_selftest.fbx. The math check for the round trip.
    /// </summary>
    public static int SelfTest(string folder, string sidecarPath, string fbxPath, string? library = null)
    {
        var lines = File.ReadLines(sidecarPath).Where(l => !l.StartsWith('#') && l.Trim().Length > 0).Select(l => l.Split('\t')).ToList();
        var first = lines[0];
        var g = ParseM(first[3]);
        // +90 yaw about the piece's own origin, then +500 x: G' = G * Tr(-t) * Rz * Tr(t) * Tr(500,0,0) (row form).
        var t = g.Translation;
        var rz = ZonePlaceholders.RotatorMatrix(new Vector3(0, 16384, 0));
        var expect = g * Matrix4x4.CreateTranslation(-t) * rz * Matrix4x4.CreateTranslation(t) * Matrix4x4.CreateTranslation(500, 0, 0);
        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(fbxPath, PostProcessSteps.None);
        static Node? Find(Node n, string name) { if (n.Name == name) return n; foreach (var c in n.Children) if (Find(c, name) is { } f) return f; return null; }
        var src = Find(scene.RootNode, first[0]);
        if (src == null) { Console.WriteLine("  first placement not found in the FBX"); return 1; }
        int ScaledMesh(int mi, float s)
        {
            var m = scene.Meshes[mi];
            var c = new Mesh(m.Name, m.PrimitiveType) { MaterialIndex = m.MaterialIndex };
            c.Vertices.AddRange(m.Vertices.Select(v => v * s));
            if (m.HasNormals) c.Normals.AddRange(m.Normals);
            if (m.HasTextureCoords(0)) { c.UVComponentCount[0] = 2; c.TextureCoordinateChannels[0].AddRange(m.TextureCoordinateChannels[0]); }
            foreach (var f in m.Faces) c.Faces.Add(new Face([.. f.Indices]));
            scene.Meshes.Add(c);
            return scene.MeshCount - 1;
        }
        Node CopyNode(Node n, Node parent, string suffix, float s)
        {
            var c = new Node(n.Name + suffix, parent) { Transform = n.Transform };
            c.MeshIndices.AddRange(s == 1f ? n.MeshIndices : n.MeshIndices.Select(mi => ScaledMesh(mi, s)));
            foreach (var k in n.Children) c.Children.Add(CopyNode(k, c, suffix, s));
            return c;
        }
        var dup = CopyNode(src, src.Parent, ".001", 1f);
        dup.Transform = ToAssimp(Swap * expect * Swap);
        src.Parent.Children.Add(dup);
        var edited = g * Matrix4x4.CreateTranslation(0, 1000, 0);
        var dup2 = CopyNode(src, src.Parent, ".002", 1.5f);
        dup2.Transform = ToAssimp(Swap * edited * Swap);
        src.Parent.Children.Add(dup2);
        // And the second placement moved in place by +300 y (an original edit: updates that component).
        var second = lines[1];
        var moved = ParseM(second[3]) * Matrix4x4.CreateTranslation(0, 300, 0);
        var src2 = Find(scene.RootNode, second[0]);
        if (src2 != null && src2.Parent == src.Parent) src2.Transform = ToAssimp(Swap * moved * Swap); else src2 = null;
        string testFbx = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fbxPath))!, Path.GetFileNameWithoutExtension(fbxPath) + "_selftest.fbx");
        ctx.ExportFile(scene, testFbx, "fbx");
        var rot = Rotator(new Matrix4x4(expect.M11, expect.M12, expect.M13, 0, expect.M21, expect.M22, expect.M23, 0, expect.M31, expect.M32, expect.M33, 0, 0, 0, 0, 1));
        Console.WriteLine($"  expected: add {first[0]}.001 at ({expect.Translation.X:0}, {expect.Translation.Y:0}, {expect.Translation.Z:0}), yaw {rot.Y * 360f / 65536f:0.#}, stock mesh");
        Console.WriteLine($"  expected: add {first[0]}.002 at ({edited.Translation.X:0}, {edited.Translation.Y:0}, {edited.Translation.Z:0}), edited mesh = stock x1.5");
        if (src2 != null) Console.WriteLine($"  expected: move {second[0]} to ({moved.Translation.X:0}, {moved.Translation.Y:0}, {moved.Translation.Z:0})");
        return Import(folder, sidecarPath, testFbx, dryRun: true, keepLighting: true, library: library);
    }

    // ---------------------------------------------------------------- import

    /// <summary>One component change in a tile: a new placement (add) or an original's new placement and/or mesh.</summary>
    sealed record Change(string Name, string Comp, bool Place, Vector3 T, Vector3 R, Vector3 S, List<ImportedSection>? Geometry);

    sealed record Part(string Name, Matrix4x4 World, int[] Meshes);

    static readonly System.Text.RegularExpressions.Regex DupSuffix = new(@"\.\d{3}$");
    // Section children: "<mesh>_s0" in our export's meshes, "<mesh>_s0001" as its nodes come back (the writer appends
    // 001), "<mesh>_s0.004" as Blender names objects (its mesh data is just "Mesh.034").
    static readonly System.Text.RegularExpressions.Regex SectionName = new(@"_s(\d+?)(?:\.?\d{3})?$");

    public static int Import(string folder, string sidecarPath, string fbxPath, bool dryRun, bool keepLighting = false, bool applyDeletes = false, string? library = null)
    {
        var orig = new Dictionary<string, (string Tile, string Comp, Matrix4x4 G)>(StringComparer.Ordinal);
        var offset = Vector3.Zero;                                             // tile coordinates = game - offset
        foreach (string line in File.ReadLines(sidecarPath))
        {
            if (line.StartsWith("# offset "))
            {
                var o = line[9..].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                offset = new Vector3(o[0], o[1], o[2]);
                continue;
            }
            if (line.StartsWith("# library ")) { library ??= Path.Combine(folder, line[10..].Trim()); continue; }
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var f = line.Split('\t');
            orig[f[0]] = (f[1], f[2], ParseM(f[3]));
        }
        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(fbxPath, PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals | PostProcessSteps.JoinIdenticalVertices);
        var world = new Dictionary<string, Matrix4x4>();
        var dups = new List<(string Name, string Id, Matrix4x4 W)>();
        var parts = new Dictionary<string, List<Part>>();                    // placement -> the geometry under it
        var unmatched = new List<string>();                                  // objects with geometry under no placement
        void Walk(Node n, Matrix4x4 parent, string? owner)
        {
            var m = FromAssimp(n.Transform) * parent;                               // row convention: local * parent
            string name = n.Name;
            if (!name.Contains("_$AssimpFbx$", StringComparison.Ordinal))              // by name: meshes hang off child nodes
            {
                string baseName = DupSuffix.Replace(name, "");
                if (orig.ContainsKey(name)) { world[name] = m; owner = name; }
                else if (orig.ContainsKey(baseName)) { dups.Add((name, baseName, m)); owner = name; }
                else if (owner == null && n.MeshCount > 0) unmatched.Add(name);
            }
            if (owner != null && n.MeshCount > 0)
            {
                if (!parts.TryGetValue(owner, out var pl)) parts[owner] = pl = [];
                pl.Add(new Part(name, m, [.. n.MeshIndices]));
            }
            foreach (var c in n.Children) Walk(c, m, owner);
        }
        Walk(scene.RootNode, Matrix4x4.Identity, null);
        var missing = orig.Keys.Where(k => !world.ContainsKey(k)).ToList();
        if (unmatched.Count > 0)
        {
            Console.WriteLine($"  {unmatched.Count} object(s) with geometry match no placement id (renamed, or not parented under a placement), ignored:");
            foreach (var u in unmatched.Take(30)) Console.WriteLine($"    {u}");
        }
        Console.WriteLine($"Placement import: {Path.GetFileName(fbxPath)}: {world.Count:N0} of {orig.Count:N0} original placements found, {dups.Count} duplicate(s), {missing.Count} missing{(dryRun ? "  [dry run]" : "")}");
        if (world.Count < 4) { Console.WriteLine("  too few originals to measure the file's axis/unit conversion"); return 1; }

        // File -> game map T (affine, row form: p_game = p_file * T) fitted from the originals' origins, refitted without
        // the ones that disagree (moved in Blender). Then Blender's local-axis conversion Lc from the originals that agree,
        // so any object's game transform is G = Lc * W * T (W = its world matrix in the file).
        var ids = world.Keys.ToList();
        var inlier = ids.ToHashSet();
        Matrix4x4 T = Matrix4x4.Identity; float rms = 0;
        for (int pass = 0; pass < 5; pass++)
        {
            var use = ids.Where(inlier.Contains).ToList();
            T = FitAffine(use.Select(k => world[k].Translation).ToList(), use.Select(k => orig[k].G.Translation).ToList(), out rms);
            var res = ids.ToDictionary(k => k, k => Vector3.Distance(Vector3.Transform(world[k].Translation, T), orig[k].G.Translation));
            var sorted = use.Select(k => res[k]).OrderBy(v => v).ToList();
            float limit = MathF.Max(0.5f, 5 * sorted[sorted.Count / 2]);
            var next = ids.Where(k => res[k] <= limit).ToHashSet();
            if (next.SetEquals(inlier)) break;
            inlier = next;
        }
        Console.WriteLine($"  file->game fit over {inlier.Count:N0} agreeing originals: rms {rms:0.###} units");
        if (rms > 1 || inlier.Count < Math.Max(4, ids.Count / 2)) { Console.WriteLine("  the originals don't agree on one conversion (too many moved?); nothing written"); return 1; }
        Matrix4x4 LocalOf(string k)
        {
            Matrix4x4.Invert(world[k] * T, out var inv);
            var l = orig[k].G * inv;
            l.M41 = l.M42 = l.M43 = 0;                                               // a pure axis conversion
            return l;
        }
        // The conversion most originals agree on (a few candidates, in case the first was rotated in place).
        var inl = ids.Where(inlier.Contains).ToList();
        var locals = inl.ToDictionary(k => k, LocalOf);
        Matrix4x4 Lc = default; int lcCount = 0;
        foreach (var cand in inl.Take(8))
        {
            int n = inl.Count(k => MaxDiff(locals[k], locals[cand], rotationOnly: true) <= 1e-3f);
            if (n > lcCount) { lcCount = n; Lc = locals[cand]; }
        }
        Console.WriteLine($"  local axis conversion agreed by {lcCount:N0} originals");
        if (lcCount < Math.Max(4, inl.Count / 2)) { Console.WriteLine("  the originals don't agree on the local axis conversion; nothing written"); return 1; }
        Matrix4x4 GameOf(Matrix4x4 W) => Lc * W * T;
        Matrix4x4.Invert(Lc, out var LcInv);

        // ---- geometry: each placement's file geometry against its stock mesh (in the mesh's own space)
        var tilePkgs = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        // A tile this sidecar was imported into before is read and rebuilt from its version before those imports: the
        // file holds all its edits, so importing on top would add its duplicates twice (Kurt keeps updating one file).
        var bases = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        byte[]? Base(string file)
        {
            if (bases.TryGetValue(file, out var b)) return b;
            b = History.BeforeSteps(Path.Combine(folder, file), ["--import-placements", Path.GetFileName(sidecarPath)], out int n);
            if (b != null) Console.WriteLine($"  {file}: starting from its version before {n} earlier import(s) of this sidecar");
            return bases[file] = b;
        }
        Package TilePkg(string file) => tilePkgs.TryGetValue(file, out var p) ? p : tilePkgs[file] = Base(file) is { } bb ? Package.FromBytes(bb) : Package.Open(Path.Combine(folder, file));
        Package? lib = null; bool libTried = false, libWarned = false;
        Dictionary<string, int>? libByName = null;
        var meshCache = new Dictionary<string, StaticMesh?>(StringComparer.OrdinalIgnoreCase);
        StaticMesh? StockMesh(string id)
        {
            var o = orig[id];
            var pkg = TilePkg(o.Tile);
            int ci = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(o.Comp, StringComparison.OrdinalIgnoreCase));
            if (ci < 0) return null;
            var c = ComponentTransform.Read(pkg, pkg.ReadExportBytes(pkg.Exports[ci]));
            if (c == null || c.MeshRef == 0) return null;
            string key = c.MeshRef > 0 ? $"{o.Tile}:{c.MeshRef}" : "lib:" + pkg.RefName(c.MeshRef);
            if (meshCache.TryGetValue(key, out var cached)) return cached;
            StaticMesh? sm = null;
            try
            {
                if (c.MeshRef > 0) sm = StaticMesh.Read(pkg, pkg.Exports[c.MeshRef - 1]);
                else
                {
                    if (!libTried)
                    {
                        libTried = true;
                        if (library != null && File.Exists(library))
                        {
                            lib = Package.Open(library);
                            libByName = new(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < lib.Exports.Length; i++)
                                if (lib.ClassOf(lib.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) libByName.TryAdd(lib.Exports[i].ObjectName, i);
                        }
                    }
                    if (lib != null && libByName!.TryGetValue(pkg.RefName(c.MeshRef), out int li)) sm = StaticMesh.Read(lib, lib.Exports[li]);
                    else if (!libWarned) { libWarned = true; Console.WriteLine("  (no region library given: mesh edits of library meshes can't be seen; pass --library)"); }
                }
            }
            catch (PackageFormatException) { }
            return meshCache[key] = sm;
        }

        // Pass 1: which placements have edited geometry, and whether the file keeps the engine's triangle winding
        // (votes from the unedited ones: each file triangle matched to a stock triangle by corner positions).
        int same = 0, reversed = 0;
        var fileTris = new Dictionary<string, List<(int Section, Vector3[] P, Vector3[] N, Vector2[] Uv)>>();
        var edited = new HashSet<string>();
        var partReport = new Dictionary<string, List<string>>();              // edited placement -> parts moved/copied in Blender
        var sectionProblems = new List<string>();
        foreach (var (owner, pl) in parts)
        {
            string id = DupSuffix.Replace(owner, "");
            if (!orig.ContainsKey(id) || StockMesh(id) is not { } sm) continue;
            var W = world.TryGetValue(owner, out var w0) ? w0 : dups.First(d => d.Name == owner).W;
            Matrix4x4.Invert(W, out var Winv);
            var tris = new List<(int, Vector3[], Vector3[], Vector2[])>();
            int kept = sm.Sections.Count(s => s.NumTriangles > 0);
            // Stock positions on a hash grid; each distinct position gets an id (UV seams share one).
            float tol = 0.05f + 1e-4f * sm.BoundsExtent.Length();
            var grid = new Dictionary<(int, int, int), List<int>>();
            var posId = new int[sm.Positions.Length];
            var ids2 = new List<Vector3>();
            (int, int, int) Cell(Vector3 q) => ((int)MathF.Floor(q.X / tol), (int)MathF.Floor(q.Y / tol), (int)MathF.Floor(q.Z / tol));
            int Nearest(Vector3 q)
            {
                var (cx, cy, cz) = Cell(q); int best = -1; float bd = tol;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l))
                        foreach (int i in l) { float d = Vector3.Distance(ids2[i], q); if (d <= bd) { bd = d; best = i; } }
                return best;
            }
            for (int v = 0; v < sm.Positions.Length; v++)
            {
                int e = Nearest(sm.Positions[v]);
                if (e < 0 || Vector3.Distance(ids2[e], sm.Positions[v]) > 1e-4f)
                {
                    e = ids2.Count; ids2.Add(sm.Positions[v]);
                    var c = Cell(sm.Positions[v]);
                    if (!grid.TryGetValue(c, out var l)) grid[c] = l = [];
                    l.Add(e);
                }
                posId[v] = e;
            }
            var sectionsAt = new List<int>[ids2.Count];                              // position id -> stock sections using it
            for (int si = 0; si < sm.Sections.Length; si++)
                for (int i = sm.Sections[si].FirstIndex; i < sm.Sections[si].FirstIndex + sm.Sections[si].NumTriangles * 3; i++)
                    (sectionsAt[posId[sm.Indices[i]]] ??= []).Add(si);
            bool bad = false;
            var partNotes = new List<string>();
            foreach (var part in pl)
            {
                var rel = part.World * Winv;
                // A part with negative scale (mirrored in Blender) turns its triangles inside out: flip them back.
                bool mirrored = rel.GetDeterminant() < 0;
                if (mirrored || rel.Translation.Length() > 0.5f || MaxDiff(rel, Matrix4x4.Identity, rotationOnly: true) > 1e-3f)
                    partNotes.Add(DupSuffix.Replace(part.Name, m => m.Value) + (mirrored ? " (mirrored: faces turned outward)" : ""));
                var A = part.World * Winv * LcInv;                                        // file vertex -> mesh space
                Matrix4x4.Invert(A, out var Ainv);
                var Nm = Matrix4x4.Transpose(Ainv);
                foreach (int mi in part.Meshes)
                {
                    var mesh = scene.Meshes[mi];
                    var mm = SectionName.Match(part.Name); if (!mm.Success) mm = SectionName.Match(mesh.Name);
                    int sec = mm.Success ? int.Parse(mm.Groups[1].Value) : kept == 1 ? Array.FindIndex(sm.Sections, s => s.NumTriangles > 0) : -1;
                    if (!mm.Success && kept > 1)
                    {
                        // No section name: the stock section most of its vertices sit on.
                        var votes = new int[sm.Sections.Length];
                        for (int v = 0; v < mesh.VertexCount; v++)
                        {
                            int id2 = Nearest(Vector3.Transform(new Vector3(mesh.Vertices[v].X, mesh.Vertices[v].Y, mesh.Vertices[v].Z), A));
                            if (id2 >= 0 && sectionsAt[id2] != null) foreach (int si in sectionsAt[id2].Distinct()) votes[si]++;
                        }
                        sec = votes.Max() > 0 ? Array.IndexOf(votes, votes.Max()) : -1;
                    }
                    if (sec < 0 || sec >= sm.Sections.Length) { bad = true; continue; }
                    foreach (var f in mesh.Faces)
                    {
                        if (f.IndexCount != 3) continue;
                        var p = new Vector3[3]; var nn = new Vector3[3]; var uv = new Vector2[3];
                        for (int k = 0; k < 3; k++)
                        {
                            int v = f.Indices[k];
                            p[k] = Vector3.Transform(new Vector3(mesh.Vertices[v].X, mesh.Vertices[v].Y, mesh.Vertices[v].Z), A);
                            nn[k] = mesh.HasNormals ? Vector3.Normalize(Vector3.TransformNormal(new Vector3(mesh.Normals[v].X, mesh.Normals[v].Y, mesh.Normals[v].Z), Nm)) : Vector3.UnitZ;
                            uv[k] = mesh.HasTextureCoords(0) ? new Vector2(mesh.TextureCoordinateChannels[0][v].X, 1f - mesh.TextureCoordinateChannels[0][v].Y) : Vector2.Zero;
                        }
                        if (mirrored) { (p[1], p[2]) = (p[2], p[1]); (nn[1], nn[2]) = (nn[2], nn[1]); (uv[1], uv[2]) = (uv[2], uv[1]); }
                        tris.Add((sec, p, nn, uv));
                    }
                }
            }
            if (bad) { sectionProblems.Add(owner); continue; }
            bool changed = false;
            var corner = new int[3];
            var stockTris = new HashSet<(int, int, int)>();
            static (int, int, int) Canon(int a, int b, int c) => a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);
            for (int i = 0; i + 2 < sm.Indices.Length; i += 3) stockTris.Add(Canon(posId[sm.Indices[i]], posId[sm.Indices[i + 1]], posId[sm.Indices[i + 2]]));
            foreach (var tr in tris)
            {
                for (int k = 0; k < 3; k++) { corner[k] = Nearest(tr.Item2[k]); if (corner[k] < 0) changed = true; }
                if (changed) break;
                if (same + reversed < 5000)
                {
                    if (stockTris.Contains(Canon(corner[0], corner[1], corner[2]))) same++;
                    else if (stockTris.Contains(Canon(corner[0], corner[2], corner[1]))) reversed++;
                }
            }
            // Same corners: compare triangles by their centres, per section present in the file. Every file triangle must
            // sit on a stock one and every stock triangle with area must be in the file. Not counts or corner identity:
            // Blender's importer drops duplicate and degenerate faces (all 4 Lowtown barbed-wire fences lost 120), and
            // near-coincident stock vertices make corner matching ambiguous (77 false edits that way).
            if (!changed)
            {
                (int, int, int) CellOf(Vector3 q) => ((int)MathF.Floor(q.X / tol), (int)MathF.Floor(q.Y / tol), (int)MathF.Floor(q.Z / tol));
                static bool Near(Dictionary<(int, int, int), List<Vector3>> g, (int, int, int) c, Vector3 q, float t)
                {
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                        if (g.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var l) && l.Any(p => Vector3.Distance(p, q) <= t)) return true;
                    return false;
                }
                void Put(Dictionary<(int, int, int), List<Vector3>> g, Vector3 q) { var c = CellOf(q); if (!g.TryGetValue(c, out var l)) g[c] = l = []; l.Add(q); }
                foreach (int si in tris.Select(t => t.Item1).Distinct())
                {
                    var sec = sm.Sections[si];
                    var stockC = new Dictionary<(int, int, int), List<Vector3>>(); var stockReal = new List<Vector3>();
                    for (int i = sec.FirstIndex; i + 2 < sec.FirstIndex + sec.NumTriangles * 3; i += 3)
                    {
                        Vector3 a = sm.Positions[sm.Indices[i]], b = sm.Positions[sm.Indices[i + 1]], c = sm.Positions[sm.Indices[i + 2]];
                        var ce = (a + b + c) / 3; Put(stockC, ce);
                        if (Vector3.Cross(b - a, c - a).Length() > 1e-4f) stockReal.Add(ce);
                    }
                    var fileC = new Dictionary<(int, int, int), List<Vector3>>();
                    foreach (var tr in tris.Where(t => t.Item1 == si))
                    {
                        var ce = (tr.Item2[0] + tr.Item2[1] + tr.Item2[2]) / 3; Put(fileC, ce);
                        if (!Near(stockC, CellOf(ce), ce, tol)) { changed = true; break; }
                    }
                    if (!changed && stockReal.Any(ce => !Near(fileC, CellOf(ce), ce, tol))) changed = true;
                    if (changed) break;
                }
            }
            if (changed) { edited.Add(owner); fileTris[owner] = tris; if (partNotes.Count > 0) partReport[owner] = partNotes; }
        }
        if (sectionProblems.Count > 0)
            Console.WriteLine($"  {sectionProblems.Count} placement(s) with geometry whose section can't be told (objects should keep their _s<N> names); geometry not checked: {string.Join(", ", sectionProblems.Take(5))}");
        bool flip = reversed > same;
        Console.WriteLine($"  triangle winding in the file: {same:N0} as stock, {reversed:N0} reversed{(flip ? " (reversing on import)" : "")}; {edited.Count} placement(s) with edited geometry");

        // Pass 2: the edited geometry, per stock section, in mesh space. Sections the file doesn't have keep their stock
        // triangles (the export leaves some out). UV0 from the file; lightmap UVs and normals from each triangle's stock
        // source (below).
        List<ImportedSection> BuildGeometry(string owner, StaticMesh sm)
        {
            var result = new List<ImportedSection>();
            var tris = fileTris[owner];
            int unmatched = 0;
            for (int s = 0; s < sm.Sections.Length; s++)
            {
                var sec = sm.Sections[s];
                var o = new ImportedSection(sec.MaterialName);
                var weld = new Dictionary<(Vector3, Vector3, Vector2, Vector2), int>();
                void Add(Vector3 p, Vector3 n, Vector2[] uvs)
                {
                    var key = (p, n, uvs[0], sm.NumTexCoords > 1 ? uvs[1] : default);
                    if (!weld.TryGetValue(key, out int i))
                    {
                        i = o.Positions.Count; weld[key] = i;
                        o.Positions.Add(p); o.Normals.Add(n);
                        for (int c = 0; c < sm.NumTexCoords && c < 4; c++) o.TexCoords[c].Add(uvs[c]);
                    }
                    o.Indices.Add(i);
                }
                var mine = tris.Where(tr => tr.Section == s).ToList();
                if (mine.Count == 0)
                {
                    for (int i = sec.FirstIndex; i < sec.FirstIndex + sec.NumTriangles * 3; i++)
                    {
                        int v = sm.Indices[i];
                        Add(sm.Positions[v], sm.Normals[v], Enumerable.Range(0, sm.NumTexCoords).Select(c => sm.TexCoords[c][v]).ToArray());
                    }
                }
                else
                {
                    // Each file triangle is matched to the stock triangle it came from: the one with the same UV0 at its
                    // three corners (a copy in Blender keeps them). It gives the lightmap UVs and the normals (turned with
                    // the copy: file-triangle frame * stock-triangle frame^-1). Among equal candidates (windows sharing
                    // one texture region) prefer one at the same position, then one agreeing with corners already
                    // assigned (a quad's two halves). Picking per vertex by nearest UV0 mixed windows: streaky lighting.
                    var range = Enumerable.Range(sec.MinVertexIndex, Math.Max(0, sec.MaxVertexIndex - sec.MinVertexIndex + 1)).ToArray();
                    if (range.Length == 0) range = Enumerable.Range(0, sm.Positions.Length).ToArray();
                    static (int, int) Q(Vector2 uv) => ((int)MathF.Round(uv.X * 1024), (int)MathF.Round(uv.Y * 1024));
                    static ((int, int), (int, int), (int, int)) Key(Vector2 a, Vector2 b, Vector2 c)
                    {
                        var l = new[] { Q(a), Q(b), Q(c) }.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToArray();
                        return (l[0], l[1], l[2]);
                    }
                    var byUv = new Dictionary<((int, int), (int, int), (int, int)), List<int>>();
                    for (int i = sec.FirstIndex; i + 2 < sec.FirstIndex + sec.NumTriangles * 3; i += 3)
                    {
                        var key = Key(sm.TexCoords[0][sm.Indices[i]], sm.TexCoords[0][sm.Indices[i + 1]], sm.TexCoords[0][sm.Indices[i + 2]]);
                        if (!byUv.TryGetValue(key, out var l)) byUv[key] = l = [];
                        l.Add(i);
                    }
                    int[][] perms = [[0, 1, 2], [1, 2, 0], [2, 0, 1], [0, 2, 1], [2, 1, 0], [1, 0, 2]];
                    float tolP = 0.05f + 1e-4f * sm.BoundsExtent.Length();
                    var assigned = new Dictionary<(Vector3, (int, int)), Vector2>();
                    static (Vector3 E1, Vector3 E2, Vector3 N)? Frame(Vector3 a, Vector3 b, Vector3 c)
                    {
                        var e1 = b - a; var n = Vector3.Cross(b - a, c - a);
                        if (e1.LengthSquared() < 1e-10f || n.LengthSquared() < 1e-12f) return null;
                        e1 = Vector3.Normalize(e1); n = Vector3.Normalize(n);
                        return (e1, Vector3.Cross(n, e1), n);
                    }
                    Vector3 RoundP(Vector3 p) => new(MathF.Round(p.X * 100) / 100, MathF.Round(p.Y * 100) / 100, MathF.Round(p.Z * 100) / 100);
                    foreach (var tr in mine)
                    {
                        int[] order = flip ? [0, 2, 1] : [0, 1, 2];
                        var P = order.Select(k => tr.P[k]).ToArray(); var N = order.Select(k => tr.N[k]).ToArray(); var U = order.Select(k => tr.Uv[k]).ToArray();
                        int[]? src = null; int bestScore = -1; bool srcSameWinding = false;
                        if (sm.NumTexCoords > 1 && byUv.TryGetValue(Key(U[0], U[1], U[2]), out var cands))
                            foreach (int ci in cands)
                                foreach (var pm in perms)
                                {
                                    int[] sv = [sm.Indices[ci + pm[0]], sm.Indices[ci + pm[1]], sm.Indices[ci + pm[2]]];
                                    if (Enumerable.Range(0, 3).Any(k => Vector2.Distance(sm.TexCoords[0][sv[k]], U[k]) > 2e-3f)) continue;
                                    int score = Enumerable.Range(0, 3).All(k => Vector3.Distance(sm.Positions[sv[k]], P[k]) <= tolP) ? 1000 : 0;
                                    for (int k = 0; k < 3; k++)
                                        if (assigned.TryGetValue((RoundP(P[k]), Q(U[k])), out var a1) && Vector2.Distance(a1, sm.TexCoords[1][sv[k]]) < 1e-5f) score += 10;
                                    bool sameWinding = pm[0] + 1 == pm[1] || (pm[0] == 2 && pm[1] == 0);
                                    if (sameWinding) score += 100000;                                           // a mirrored match only if nothing else
                                    if (score > bestScore) { bestScore = score; src = sv; srcSameWinding = sameWinding; }
                                }
                        var fs = src == null ? null : Frame(sm.Positions[src[0]], sm.Positions[src[1]], sm.Positions[src[2]]);
                        var ff = Frame(P[0], P[1], P[2]);
                        for (int k = 0; k < 3; k++)
                        {
                            var uvs = new Vector2[Math.Max(1, sm.NumTexCoords)];
                            uvs[0] = U[k];
                            var n = N[k];
                            if (src != null)
                            {
                                for (int c = 1; c < sm.NumTexCoords; c++) uvs[c] = sm.TexCoords[c][src[k]];
                                if (srcSameWinding && fs is { } a && ff is { } b)
                                {
                                    var sn = sm.Normals[src[k]];
                                    n = Vector3.Normalize(Vector3.Dot(sn, a.E1) * b.E1 + Vector3.Dot(sn, a.E2) * b.E2 + Vector3.Dot(sn, a.N) * b.N);
                                }
                            }
                            else if (sm.NumTexCoords > 1)
                            {
                                int best = range.MinBy(v => Vector2.DistanceSquared(sm.TexCoords[0][v], U[k]) * 1e6f + Vector3.DistanceSquared(sm.Positions[v], P[k]));
                                for (int c = 1; c < sm.NumTexCoords; c++) uvs[c] = sm.TexCoords[c][best];
                                unmatched++;
                            }
                            if (sm.NumTexCoords > 1) assigned[(RoundP(P[k]), Q(U[k]))] = uvs[1];
                            Add(P[k], n, uvs);
                        }
                    }
                }
                result.Add(o);
            }
            if (unmatched > 0) Console.WriteLine($"  {owner}: {unmatched / 3:N0} triangle(s) with UVs no stock triangle has (edited UVs): lightmap UVs from the nearest stock vertex");
            return result;
        }

        var adds = new Dictionary<string, List<Change>>();
        var changes = new Dictionary<string, List<Change>>();
        var deletes = new Dictionary<string, List<(string Name, string Comp)>>();
        bool Place(string name, Matrix4x4 G, out Vector3 rot, out Vector3 s)
        {
            // G = S * R * Tr (row form): scale = row lengths of the 3x3, rotation = the normalised rows.
            rot = default;
            var r0 = new Vector3(G.M11, G.M12, G.M13); var r1 = new Vector3(G.M21, G.M22, G.M23); var r2 = new Vector3(G.M31, G.M32, G.M33);
            s = new Vector3(r0.Length(), r1.Length(), r2.Length());
            if (Vector3.Dot(Vector3.Cross(r0, r1), r2) < 0) { Console.WriteLine($"  {name}: mirrored (negative scale); not supported, skipped"); return false; }
            var R = new Matrix4x4(r0.X / s.X, r0.Y / s.X, r0.Z / s.X, 0, r1.X / s.Y, r1.Y / s.Y, r1.Z / s.Y, 0, r2.X / s.Z, r2.Y / s.Z, r2.Z / s.Z, 0, 0, 0, 0, 1);
            rot = Rotator(R);
            if (MaxDiff(ZonePlaceholders.RotatorMatrix(rot), R, rotationOnly: true) > 0.01f) { Console.WriteLine($"  {name}: rotation doesn't decompose cleanly (skewed?); skipped"); return false; }
            return true;
        }
        void Report(string what, string name, Matrix4x4 G, Vector3 rot, Vector3 s, string tile, bool geo) =>
            Console.WriteLine($"  {what} {name}: in-game ({G.Translation.X:0}, {G.Translation.Y:0}, {G.Translation.Z:0}) = tile ({G.Translation.X - offset.X:0}, {G.Translation.Y - offset.Y:0}, {G.Translation.Z - offset.Z:0}), yaw {rot.Y * 360f / 65536f:0.#}, pitch {rot.X * 360f / 65536f:0.#}, roll {rot.Z * 360f / 65536f:0.#}, scale ({s.X:0.###}, {s.Y:0.###}, {s.Z:0.###}){(geo ? ", edited mesh" : "")} -> {tile}");
        void AddTo<TV>(Dictionary<string, List<TV>> d, string tile, TV v) { if (!d.TryGetValue(tile, out var l)) d[tile] = l = []; l.Add(v); }
        void Parts(string key) { if (partReport.TryGetValue(key, out var pn)) Console.WriteLine($"      parts moved or copied in Blender: {string.Join(", ", pn)}"); }

        foreach (var (name, id, W) in dups)
        {
            var G = GameOf(W);
            if (!Place(name, G, out var rot, out var s)) continue;
            var geo = edited.Contains(name) ? BuildGeometry(name, StockMesh(id)!) : null;
            AddTo(adds, orig[id].Tile, new Change(name, orig[id].Comp, true, G.Translation - offset, rot, s, geo));
            Report("add ", name, G, rot, s, orig[id].Tile, geo != null);
            Parts(name);
        }
        // Originals: moved (position off by more than 0.5 units or 3x the fit's rms, or rotation / scale changed) and/or
        // with edited geometry.
        float moveTol = MathF.Max(0.5f, 3 * rms);
        foreach (var id in ids)
        {
            var og = orig[id].G; var G = GameOf(world[id]);
            float size = MathF.Max(1f, new Vector3(og.M11, og.M12, og.M13).Length());
            bool moved = Vector3.Distance(G.Translation, og.Translation) > moveTol || MaxDiff(G, og, rotationOnly: true) > 2e-3f * size;
            bool geoEdit = edited.Contains(id);
            if (!moved && !geoEdit) continue;
            Vector3 rot = default, s = default;
            if (moved && !Place(id, G, out rot, out s)) continue;
            var geo = geoEdit ? BuildGeometry(id, StockMesh(id)!) : null;
            AddTo(changes, orig[id].Tile, new Change(id, orig[id].Comp, moved, G.Translation - offset, rot, s, geo));
            if (moved) Report("move", id, G, rot, s, orig[id].Tile, geo != null);
            else Console.WriteLine($"  mesh {id}: edited geometry, placement unchanged -> {orig[id].Tile}");
            Parts(id);
        }
        // Originals missing from the file: taken off their collection actor's list, only with --apply-deletes.
        if (missing.Count > 0)
        {
            if (!applyDeletes) Console.WriteLine($"  {missing.Count:N0} original(s) not in the file: left as they are (--apply-deletes removes them)");
            else if (missing.Count > orig.Count / 2) { Console.WriteLine($"  --apply-deletes: {missing.Count:N0} of {orig.Count:N0} originals missing looks like a partial export; nothing written"); return 1; }
            else
                foreach (var id in missing)
                {
                    AddTo(deletes, orig[id].Tile, (id, orig[id].Comp));
                    Console.WriteLine($"  delete {id} -> {orig[id].Tile}");
                }
        }
        var rebased = bases.Where(kv => kv.Value != null).Select(kv => kv.Key).ToList();
        if (adds.Count + changes.Count + deletes.Count + rebased.Count == 0) { Console.WriteLine("  nothing to change (duplicates are named like the original plus .001, .002 ...)"); return 0; }

        // The undo history names this step after the sidecar whatever the caller (CLI or GUI): a later import of the same
        // sidecar finds it by that name to start from the version before it.
        History.Label = $"--import-placements {Path.GetFileName(sidecarPath)} {Path.GetFileName(fbxPath)}";
        int failures = 0;
        foreach (string tile in adds.Keys.Concat(changes.Keys).Concat(deletes.Keys).Concat(rebased).Distinct(StringComparer.OrdinalIgnoreCase))
            if (ApplyToTile(folder, tile, library, adds.GetValueOrDefault(tile) ?? [], changes.GetValueOrDefault(tile) ?? [], deletes.GetValueOrDefault(tile) ?? [], dryRun, keepLighting, bases.GetValueOrDefault(tile)) != 0) failures++;
        return failures == 0 ? 0 : 1;
    }

    static float MaxDiff(Matrix4x4 a, Matrix4x4 b, bool rotationOnly = false)
    {
        float[] x = [a.M11, a.M12, a.M13, a.M21, a.M22, a.M23, a.M31, a.M32, a.M33, a.M41, a.M42, a.M43];
        float[] y = [b.M11, b.M12, b.M13, b.M21, b.M22, b.M23, b.M31, b.M32, b.M33, b.M41, b.M42, b.M43];
        int n = rotationOnly ? 9 : 12; float m = 0;
        for (int i = 0; i < n; i++) m = MathF.Max(m, MathF.Abs(x[i] - y[i]));
        return m;
    }

    /// <summary>UE rotator (pitch, yaw, roll in 65536ths) of a pure rotation, matching ZonePlaceholders.RotatorMatrix.</summary>
    static Vector3 Rotator(Matrix4x4 R)
    {
        var x = new Vector3(R.M11, R.M12, R.M13);
        float pitch = MathF.Atan2(x.Z, MathF.Sqrt(x.X * x.X + x.Y * x.Y)), yaw = MathF.Atan2(x.Y, x.X);
        float k = 65536f / (MathF.PI * 2f);
        var noRoll = ZonePlaceholders.RotatorMatrix(new Vector3(pitch * k, yaw * k, 0));
        var sy = new Vector3(noRoll.M21, noRoll.M22, noRoll.M23);
        var y = new Vector3(R.M21, R.M22, R.M23); var z = new Vector3(R.M31, R.M32, R.M33);
        float roll = MathF.Atan2(Vector3.Dot(z, sy), Vector3.Dot(y, sy));
        return new Vector3(MathF.Round(pitch * k), MathF.Round(yaw * k), MathF.Round(roll * k));
    }

    /// <summary>Least-squares affine map a -> b (row form, 4x4 with last column 0,0,0,1).</summary>
    static Matrix4x4 FitAffine(List<Vector3> a, List<Vector3> b, out float rms)
    {
        // Normal equations: (A^T A) X = A^T B, A rows = (ax, ay, az, 1), solved per output column with doubles.
        var ata = new double[4, 4]; var atb = new double[4, 3];
        for (int i = 0; i < a.Count; i++)
        {
            double[] r = [a[i].X, a[i].Y, a[i].Z, 1]; double[] o = [b[i].X, b[i].Y, b[i].Z];
            for (int p = 0; p < 4; p++) { for (int q = 0; q < 4; q++) ata[p, q] += r[p] * r[q]; for (int c = 0; c < 3; c++) atb[p, c] += r[p] * o[c]; }
        }
        var x = Solve(ata, atb);
        var m = new Matrix4x4((float)x[0, 0], (float)x[0, 1], (float)x[0, 2], 0, (float)x[1, 0], (float)x[1, 1], (float)x[1, 2], 0,
                              (float)x[2, 0], (float)x[2, 1], (float)x[2, 2], 0, (float)x[3, 0], (float)x[3, 1], (float)x[3, 2], 1);
        double sum = 0;
        for (int i = 0; i < a.Count; i++) sum += Vector3.DistanceSquared(Vector3.Transform(a[i], m), b[i]);
        rms = (float)Math.Sqrt(sum / a.Count);
        return m;
    }

    static double[,] Solve(double[,] A, double[,] B)
    {
        int n = 4, k = B.GetLength(1);
        var a = (double[,])A.Clone(); var b = (double[,])B.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col; for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
            if (Math.Abs(a[piv, col]) < 1e-12) throw new InvalidDataException("originals are too few or all in one plane to fit the conversion");
            for (int c = 0; c < n; c++) (a[col, c], a[piv, c]) = (a[piv, c], a[col, c]);
            for (int c = 0; c < k; c++) (b[col, c], b[piv, c]) = (b[piv, c], b[col, c]);
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = a[r, col] / a[col, col];
                for (int c = 0; c < n; c++) a[r, c] -= f * a[col, c];
                for (int c = 0; c < k; c++) b[r, c] -= f * b[col, c];
            }
        }
        var x = new double[n, k];
        for (int r = 0; r < n; r++) for (int c = 0; c < k; c++) x[r, c] = b[r, c] / a[r, r];
        return x;
    }

    static readonly string[] PlacementTags = ["Translation", "Rotation", "Scale3D", "Scale", "VisibilityId", "VertexPositionVersionNumber"];

    /// <summary>A component's bytes with a new placement (Translation, Rotation, Scale3D); its lighting kept or emptied.</summary>
    static byte[] Placed(Package pkg, byte[] src, TagWriter tw, Vector3 t, Vector3 r, Vector3 s, bool keepLighting)
    {
        var tags = TagWalker.Walk(pkg, src, 8) ?? throw new InvalidDataException("component properties don't parse");
        using var ms = new MemoryStream();
        ms.Write(src, 0, 8);
        foreach (var tg in tags) if (!PlacementTags.Any(x => tg.Name.Equals(x, StringComparison.OrdinalIgnoreCase))) ms.Write(src, tg.Start, tg.End - tg.Start);
        ms.Write(tw.Tag("Translation", "StructProperty", "Vector", [.. BitConverter.GetBytes(t.X), .. BitConverter.GetBytes(t.Y), .. BitConverter.GetBytes(t.Z)]));
        ms.Write(tw.Tag("Rotation", "StructProperty", "Rotator", [.. BitConverter.GetBytes((int)r.X), .. BitConverter.GetBytes((int)r.Y), .. BitConverter.GetBytes((int)r.Z)]));
        ms.Write(tw.Tag("Scale3D", "StructProperty", "Vector", [.. BitConverter.GetBytes(s.X), .. BitConverter.GetBytes(s.Y), .. BitConverter.GetBytes(s.Z)]));
        ms.Write(src, tags.NoneAt, 8);
        // --keep-lighting: the original's lighting record (its light/shadow maps: same mesh, so the copy lights like the
        // original: new walls were dark without it); otherwise nothing baked.
        if (keepLighting) ms.Write(src, tags.NoneAt + 8, src.Length - tags.NoneAt - 8); else ms.Write(EmptyLighting);
        return ms.ToArray();
    }

    /// <summary>The component with its StaticMesh reference replaced.</summary>
    static byte[] WithMesh(Package pkg, byte[] bytes, int meshRef)
    {
        var t = TagWalker.Walk(pkg, bytes, 8)?.FirstOrDefault(x => x.Name.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException("component has no StaticMesh property");
        var o = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(t.ValueAt), meshRef);
        return o;
    }

    /// <summary>The component with an empty lighting record (nothing baked).</summary>
    static byte[] WithEmptyLighting(Package pkg, byte[] bytes)
    {
        var tags = TagWalker.Walk(pkg, bytes, 8) ?? throw new InvalidDataException("component properties don't parse");
        return [.. bytes.AsSpan(0, tags.NoneAt + 8), .. EmptyLighting];
    }

    /// <summary>
    /// Whether a component's lighting record (after its properties) fits any mesh: no shadow-vertex buffers and no
    /// vertex light map (both hold one value per vertex of the original mesh). A texture light map only needs sensible
    /// lightmap UVs. Layout: LOD count, then per LOD ShadowMaps (refs), ShadowVertexBuffers (refs), light map type
    /// (0 none, 1 vertex, 2 texture): wall_a's is 1, 0, 0, 2.
    /// </summary>
    static bool LightingFitsAnyMesh(Package pkg, byte[] bytes)
    {
        var tags = TagWalker.Walk(pkg, bytes, 8);
        if (tags == null) return false;
        int p = tags.NoneAt + 8;
        if (p + 4 > bytes.Length) return true;
        int lods = BitConverter.ToInt32(bytes, p);
        if (lods == 0) return true;
        if (lods != 1 || p + 16 > bytes.Length) return false;
        p += 4;
        int shadowMaps = BitConverter.ToInt32(bytes, p); p += 4 + 4 * shadowMaps;
        if (shadowMaps < 0 || p + 8 > bytes.Length) return false;
        int shadowVbs = BitConverter.ToInt32(bytes, p); p += 4 + 4 * shadowVbs;
        if (shadowVbs != 0 || p + 4 > bytes.Length) return false;
        return BitConverter.ToInt32(bytes, p) != 1;
    }

    static List<int> ActorList(Package pkg, byte[] a)
    {
        var l = TagWalker.Walk(pkg, a, 4)?.FirstOrDefault(t => t.Name.Equals("StaticMeshComponents", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("collection actor has no StaticMeshComponents");
        return Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(l.ValueAt))).Select(k => BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(l.ValueAt + 4 + 4 * k))).ToList();
    }

    static byte[] WithList(Package pkg, byte[] src, TagWriter tw, List<int> list)
    {
        var tags = TagWalker.Walk(pkg, src, 4) ?? throw new InvalidDataException("collection actor properties don't parse");
        var lt = tags.First(t => t.Name.Equals("StaticMeshComponents", StringComparison.OrdinalIgnoreCase));
        using var ms = new MemoryStream();
        ms.Write(src, 0, 4);
        foreach (var t in tags)
            if (t == lt) ms.Write(tw.Tag("StaticMeshComponents", "ArrayProperty", null, [.. BitConverter.GetBytes(list.Count), .. list.SelectMany(BitConverter.GetBytes)]));
            else ms.Write(src, t.Start, t.End - t.Start);
        ms.Write(src, tags.NoneAt, src.Length - tags.NoneAt);
        return ms.ToArray();
    }

    /// <summary>Path of an export or import reference, as --copy-export's --replace-ref takes it.</summary>
    static string RefPath(Package pkg, int r)
    {
        if (r > 0) return pkg.PathOf(pkg.Exports[r - 1]);
        var parts = new List<string>();
        for (int guard = 0; r != 0 && guard < 32; guard++)
        {
            if (r < 0) { var im = pkg.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
            else { parts.Insert(0, pkg.PathOf(pkg.Exports[r - 1])); break; }
        }
        return string.Join('.', parts);
    }

    /// <summary>
    /// One tile's changes in one verified write: added components (copies of their original), changed originals (new
    /// placement with their own lighting kept, and/or a new mesh) and deleted originals (off their collection actor's
    /// list; their data stays). Edited geometry first becomes a new StaticMesh in the tile: a copy of the stock mesh
    /// (from the tile or the region library) named &lt;mesh&gt;_&lt;tile&gt;_e&lt;N&gt; (unique across tiles: objects are identified
    /// by path), its section materials and body setup nulled (the component supplies the materials, as meshes cooked
    /// into tiles have), then rebuilt from the file geometry exactly as --import-fbx builds (no collision). These steps
    /// run on a scratch copy; the live file is written once. Components placed by a standalone actor can't be changed
    /// or deleted this way: reported and skipped.
    /// </summary>
    static int ApplyToTile(string folder, string tile, string? library, List<Change> adds, List<Change> changes, List<(string Name, string Comp)> deletes, bool dryRun, bool keepLighting, byte[]? baseBytes = null)
    {
        string live = Path.GetFullPath(Path.Combine(folder, tile));
        if (Program.IsBackupName(live)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        string work = live;
        var meshFor = new Dictionary<string, int>();                            // change name -> its new mesh
        var edits = adds.Concat(changes).Where(c => c.Geometry != null).ToList();
        string outFile = Path.Combine(AppContext.BaseDirectory, "import_out", tile);
        if (edits.Count > 0 || baseBytes != null)
        {
            // Scratch copy: of the version before this sidecar's earlier imports, or of the live file.
            string dir = Path.Combine(Path.GetTempPath(), "MhoPackageModifier_placements");
            Directory.CreateDirectory(dir);
            work = Path.Combine(dir, tile);
            if (baseBytes != null) File.WriteAllBytes(work, baseBytes); else File.Copy(live, work, overwrite: true);
        }
        if (edits.Count > 0)
        {
            Package? lib = null;
            string tileTag = Path.GetFileNameWithoutExtension(tile);
            foreach (var ch in edits)
            {
                var p0 = Package.Open(work);
                int comp = Array.FindIndex(p0.Exports, e => p0.PathOf(e).Equals(ch.Comp, StringComparison.OrdinalIgnoreCase));
                if (comp < 0) { Console.WriteLine($"  {ch.Name}: component {ch.Comp} not found in {tile}"); return 1; }
                var c = ComponentTransform.Read(p0, p0.ReadExportBytes(p0.Exports[comp]))!;
                string srcPath; Package srcPkg; int srcIndex;
                if (c.MeshRef > 0) { srcPath = work; srcPkg = p0; srcIndex = c.MeshRef - 1; }
                else
                {
                    if (library == null || !File.Exists(library)) { Console.WriteLine($"  {ch.Name}: its mesh is in the region library; pass --library <pkg>. Nothing written."); return 1; }
                    lib ??= Package.Open(library);
                    string meshName = p0.RefName(c.MeshRef);
                    srcPath = library; srcPkg = lib;
                    srcIndex = Array.FindIndex(lib.Exports, e => e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && lib.ClassOf(e).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
                    if (srcIndex < 0) { Console.WriteLine($"  {ch.Name}: mesh {meshName} not in {Path.GetFileName(library)}. Nothing written."); return 1; }
                }
                var sm = StaticMesh.Read(srcPkg, srcPkg.Exports[srcIndex]);
                string meshPath = srcPkg.PathOf(srcPkg.Exports[srcIndex]);
                var nulls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in sm.Sections) if (s.MaterialRef != 0) nulls[RefPath(srcPkg, s.MaterialRef)] = "none";
                int bodySetup = BitConverter.ToInt32(sm.Layout.Original, sm.Layout.BoundsAt + 28);
                if (bodySetup != 0) nulls[RefPath(srcPkg, bodySetup)] = "none";
                string outer = meshPath.Contains('.') ? meshPath[..(meshPath.LastIndexOf('.') + 1)] : "";
                string baseName = srcPkg.Exports[srcIndex].ObjectName, newName;
                int n = 1;
                do newName = $"{baseName}_{tileTag}_e{n++}"; while (p0.Exports.Any(e => p0.PathOf(e).Equals(outer + newName, StringComparison.OrdinalIgnoreCase)));
                if (File.Exists(outFile)) File.Delete(outFile);
                int rc = ExportCopy.Run(srcPath, meshPath, work, [], dryRun: true, rename: newName, replaceRefs: nulls);
                if (rc != 0 || !File.Exists(outFile)) { Console.WriteLine($"  {ch.Name}: copying the mesh failed. Nothing written."); return 1; }
                File.Copy(outFile, work, overwrite: true);
                var p1 = Package.Open(work);
                int mi = Array.FindIndex(p1.Exports, e => p1.PathOf(e).Equals(outer + newName, StringComparison.OrdinalIgnoreCase));
                if (mi < 0) { Console.WriteLine($"  {ch.Name}: copied mesh not found. Nothing written."); return 1; }
                byte[] bytes; List<string> probs; BuiltMesh built;
                try
                {
                    built = StaticMeshBuilder.BuildBySection(StaticMesh.Read(p1, p1.Exports[mi]), ch.Geometry!);
                    bytes = MeshImport.ReplaceMesh(p1, mi, built, out probs);
                }
                catch (InvalidDataException ex) { Console.WriteLine($"  {ch.Name}: can't build the edited mesh: {ex.Message}. Nothing written."); return 1; }
                if (probs.Count > 0) { Console.WriteLine($"  {ch.Name}: edited mesh verify FAIL"); probs.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
                File.WriteAllBytes(work, bytes);
                meshFor[ch.Name] = mi + 1;
                Console.WriteLine($"  {ch.Name}: edited mesh -> {outer}{newName}: {built.Positions.Length:N0} verts, {built.Indices.Length / 3:N0} tris (stock {sm.Positions.Length:N0} / {sm.Indices.Length / 3:N0}), bounds {built.BoundsOrigin - built.BoundsExtent} .. {built.BoundsOrigin + built.BoundsExtent} (stock {sm.BoundsOrigin - sm.BoundsExtent} .. {sm.BoundsOrigin + sm.BoundsExtent})");
            }
        }

        var pkg = Package.Open(work);
        int Find(string path) => Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
        bool InCollection(int comp) => pkg.Exports[comp].OuterIndex > 0
            && pkg.ClassOf(pkg.Exports[pkg.Exports[comp].OuterIndex - 1]).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase);
        int firstActor = Array.FindIndex(pkg.Exports, e => pkg.ClassOf(e).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase));
        if (firstActor < 0) { Console.WriteLine($"  {tile}: no StaticMeshCollectionActor"); return 1; }
        var addNames = new List<string>();
        foreach (string n in new[] { "Translation", "Rotation", "Scale3D", "Scale", "StructProperty", "FloatProperty", "Vector", "Rotator" })
            if (!pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) addNames.Add(n);
        var tw = new TagWriter(pkg, addNames);
        var add = new List<NewExport>();
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var lists = new Dictionary<int, List<int>>();                          // collection actor -> its new list
        List<int> ListOf(int actor) => lists.TryGetValue(actor, out var l) ? l : lists[actor] = ActorList(pkg, pkg.ReadExportBytes(pkg.Exports[actor]));
        var expect = new List<(int Ref, int Mesh, Vector3 T, Vector3 R, Vector3 S)>();
        // Name numbers for new components: above every number already in the table (900000+ is ours; an earlier
        // import into this tile used some already, and reusing one gave two exports the same path).
        int nextNumber = 900000;
        for (int i = 0; i < pkg.Exports.Length; i++) nextNumber = Math.Max(nextNumber, BinaryPrimitives.ReadInt32LittleEndian(pkg.Body.AsSpan(pkg.ExportEntryStart[i] + 16)) + 1);
        int k = 0, moved = 0, meshes = 0, removed = 0;
        byte[] Relit(string name, byte[] bytes, bool newMesh)
        {
            if (!newMesh || LightingFitsAnyMesh(pkg, bytes)) return bytes;
            Console.WriteLine($"  {name}: its lighting record holds per-vertex data for the stock mesh; the edited mesh gets empty lighting (may look darker)");
            return WithEmptyLighting(pkg, bytes);
        }

        foreach (var ch in adds)
        {
            int comp = Find(ch.Comp);
            if (comp < 0) { Console.WriteLine($"  {ch.Name}: component {ch.Comp} not found in {tile}"); return 1; }
            byte[] src = pkg.ReadExportBytes(pkg.Exports[comp]);
            byte[] bytes = Placed(pkg, src, tw, ch.T, ch.R, ch.S, keepLighting);
            int mesh = ComponentTransform.Read(pkg, src)!.MeshRef;
            if (meshFor.TryGetValue(ch.Name, out int nm)) { bytes = Relit(ch.Name, WithMesh(pkg, bytes, nm), true); mesh = nm; meshes++; }
            // The new component goes into the original's collection actor (the first one if a standalone actor owned it).
            int actor = InCollection(comp) ? pkg.Exports[comp].OuterIndex - 1 : firstActor;
            byte[] entry = pkg.Body.AsSpan(pkg.ExportEntryStart[comp], pkg.ExportEntryEnd[comp] - pkg.ExportEntryStart[comp]).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), actor + 1);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), nextNumber + k);    // a name number nothing else in the table uses
            int newRef = pkg.Exports.Length + add.Count + 1;
            byte[] final = bytes;
            add.Add(new NewExport(comp, nextNumber + k, _ => final) { Entry = entry });
            ListOf(actor).Add(newRef);
            expect.Add((newRef, mesh, ch.T, ch.R, ch.S));
            k++;
        }
        foreach (var ch in changes)
        {
            int comp = Find(ch.Comp);
            if (comp < 0 || !InCollection(comp)) { Console.WriteLine($"  {ch.Name}: {(comp < 0 ? "not found" : "placed by its own actor")}; change skipped"); continue; }
            byte[] src = pkg.ReadExportBytes(pkg.Exports[comp]);
            var c = ComponentTransform.Read(pkg, src)!;
            byte[] bytes = ch.Place ? Placed(pkg, src, tw, ch.T, ch.R, ch.S, keepLighting: true) : src;   // a moved piece keeps its own lighting
            int mesh = c.MeshRef;
            if (meshFor.TryGetValue(ch.Name, out int nm)) { bytes = Relit(ch.Name, WithMesh(pkg, bytes, nm), true); mesh = nm; meshes++; }
            byte[] final = bytes;
            replace[comp] = _ => final;
            expect.Add((comp + 1, mesh, ch.Place ? ch.T : c.Translation, ch.Place ? ch.R : c.Rotation, ch.Place ? ch.S : c.Scale));
            if (ch.Place) moved++;
        }
        foreach (var (name, compPath) in deletes)
        {
            int comp = Find(compPath);
            if (comp < 0 || !InCollection(comp)) { Console.WriteLine($"  {name}: {(comp < 0 ? "not found" : "placed by its own actor")}; delete skipped"); continue; }
            if (ListOf(pkg.Exports[comp].OuterIndex - 1).Remove(comp + 1)) removed++;
        }
        foreach (var (actor, list) in lists) { byte[] ab = WithList(pkg, pkg.ReadExportBytes(pkg.Exports[actor]), tw, list); replace[actor] = _ => ab; }
        if (replace.Count == 0 && add.Count == 0 && baseBytes == null) { Console.WriteLine($"  {tile}: nothing to change"); return 0; }

        // Nothing left to change on a tile this sidecar changed before: it goes back to its version before those imports.
        bool restoreOnly = replace.Count == 0 && add.Count == 0;
        byte[] output = baseBytes!;
        var written = default(Dictionary<int, byte[]>?);
        if (!restoreOnly) { output = PackageRebuilder.Rebuild(pkg, replace, add, out var w0, addNames); written = w0; }
        List<string> Check(byte[] b)
        {
            if (written == null) return b.AsSpan().SequenceEqual(baseBytes) ? [] : ["the file isn't its version before this sidecar's imports"];
            var problems = PackageRebuilder.Verify(pkg, b, [.. replace.Keys], add, written, addNames);
            var w = Package.FromBytes(b);
            foreach (var (rf, mesh, t, r, s) in expect)
            {
                var c = ComponentTransform.Read(w, w.ReadExportBytes(w.Exports[rf - 1]));
                if (c == null || c.MeshRef != mesh || Vector3.Distance(c.Translation, t) > 0.01f || c.Rotation != r || Vector3.Distance(c.Scale, s) > 1e-4f)
                    problems.Add($"component #{rf} doesn't read back with its mesh and placement");
            }
            foreach (var (actor, list) in lists)
                if (!ActorList(w, w.ReadExportBytes(w.Exports[actor])).SequenceEqual(list)) problems.Add($"{w.Exports[actor].ObjectName}: component list doesn't read back as built");
            var paths = w.Exports.Select(e => w.PathOf(e)).ToList();
            if (paths.Count != paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()) problems.Add("two exports share a path");
            return problems;
        }
        var problems = Check(output);
        Console.WriteLine($"  {tile}: {add.Count} added, {moved} moved, {meshes} with an edited mesh, {removed} removed{(dryRun ? "  [dry run]" : "")}");
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine("  verify: PASS (new and changed components read back with their mesh and placement; actor lists as built; every other export identical)");
        if (work != live) File.Delete(work);
        if (dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, output);
            return 0;
        }
        return MeshImport.WriteLive(live, output, Check) ? 0 : 1;
    }
}
