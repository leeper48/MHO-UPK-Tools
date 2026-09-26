using System.Numerics;
using Assimp;

namespace UpkMeshScan;

/// <summary>
/// --export-placed: the real placed meshes of a zone's tiles, in world position, with the materials their components
/// give them and those materials' textures — one FBX for Blender (e.g. to bake the real Hightown streets onto the LOD
/// ground). Placement as --zone-placeholders reads it (component transform, composed with a standalone owning actor's),
/// plus --offset (tile coordinates -> in-game, Hightown +5208,-15512). A filter keeps ground-level pieces: world top
/// at most --max-z and bottom at least --min-z, and (--max-height) flat pieces only: roads, sidewalks, plazas, not props. One Assimp mesh per
/// material; textures (largest stored mip, .tfc included) go to &lt;name&gt;_textures\. Read-only on the game files.
/// </summary>
static class PlacedExport
{
    public static int Run(string folder, string layout, string libraryPath, string outFbx, Vector3 offset, float minZ, float maxZ, float minFootprint, string[] skip, float maxHeight = float.MaxValue, string[]? skipMaterials = null, IReadOnlyList<(string Material, string Texture)>? diffuseOverride = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var files = Directory.EnumerateFiles(folder, "*.upk").Where(f => !Program.IsBackupName(f))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
        var tiles = new List<(string Path, Vector3 Offset)>();
        foreach (string line in File.ReadLines(layout))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            string[] f = line.Split('\t');
            if (!files.TryGetValue(f[0], out string? path)) { Console.WriteLine($"  tile {f[0]} not found, skipped"); continue; }
            tiles.Add((path, new Vector3(float.Parse(f[1], inv), float.Parse(f[2], inv), 0)));
        }
        var library = Package.Open(libraryPath);
        var libByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var libByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < library.Exports.Length; i++)
        {
            libByPath.TryAdd(library.PathOf(library.Exports[i]), i);
            if (library.ClassOf(library.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) libByName.TryAdd(library.Exports[i].ObjectName, i);
        }
        Console.WriteLine($"Placed-mesh export: {tiles.Count} tile(s) from {Path.GetFileName(layout)}, library {Path.GetFileName(libraryPath)}, world z {minZ}..{maxZ}, offset {offset}");

        string outDir = Path.GetDirectoryName(Path.GetFullPath(outFbx))!;
        string texFolder = Path.GetFileNameWithoutExtension(outFbx) + "_textures";
        Directory.CreateDirectory(Path.Combine(outDir, texFolder));
        string cacheFolder = Path.GetDirectoryName(Path.GetFullPath(libraryPath))!;

        var meshes = new Dictionary<string, StaticMesh?>();
        var parts = new Dictionary<string, (Mesh Mesh, string Diffuse)>();   // material path -> merged geometry
        var scene = new Scene { RootNode = new Node(Path.GetFileNameWithoutExtension(outFbx)) };
        var texWritten = new Dictionary<string, string>();
        var report = new List<string>();
        int placedCount = 0, kept = 0, skippedSections = 0;

        // Resolve a material reference seen in package p to (package, export index, path).
        (Package Pkg, int Index, string Path)? Material(Package p, int r)
        {
            if (r > 0) return (p, r - 1, p.PathOf(p.Exports[r - 1]));
            if (r < 0)
            {
                var parts2 = new List<string>();
                for (int guard = 0; r < 0 && guard < 16; guard++) { var im = p.Imports[-r - 1]; parts2.Insert(0, im.ObjectName); r = im.OuterIndex; }
                string path = string.Join('.', parts2);
                return libByPath.TryGetValue(path, out int li) ? (library, li, path) : null;
            }
            return null;
        }

        foreach (var (tilePath, tileOffset) in tiles)
        {
            var pkg = Package.Open(tilePath);
            foreach (var e in pkg.Exports)
            {
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
                placedCount++;
                Package owner; int index;
                if (c.MeshRef > 0) { owner = pkg; index = c.MeshRef - 1; }
                else if (libByName.TryGetValue(meshName, out int li)) { owner = library; index = li; }
                else continue;
                string key = $"{(owner == library ? "lib" : tilePath)}:{index}";
                if (!meshes.TryGetValue(key, out var mesh))
                {
                    try { mesh = StaticMesh.Read(owner, owner.Exports[index]); } catch (PackageFormatException) { mesh = null; }
                    meshes[key] = mesh;
                }
                if (mesh == null) continue;
                var m = ZonePlaceholders.RotatorMatrix(rot);
                var w = mesh.Positions.Select(v => Vector3.Transform(v * sc, m) + t + tileOffset + offset).ToArray();
                Vector3 lo = w.Aggregate(Vector3.Min), hi = w.Aggregate(Vector3.Max);
                if (hi.Z - offset.Z > maxZ || lo.Z - offset.Z < minZ || MathF.Max(hi.X - lo.X, hi.Y - lo.Y) < minFootprint || hi.Z - lo.Z > maxHeight) continue;
                kept++;

                // Component materials by section index (Materials array), else the mesh section's own.
                var tags = TagWalker.Walk(pkg, d, 8);
                var mt = tags?.FirstOrDefault(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
                var compMats = new List<int>();
                if (mt != null) { int n = BitConverter.ToInt32(d, mt.ValueAt); for (int k = 0; k < n; k++) compMats.Add(BitConverter.ToInt32(d, mt.ValueAt + 4 + 4 * k)); }
                for (int s = 0; s < mesh.Sections.Length; s++)
                {
                    var sec = mesh.Sections[s];
                    if (sec.NumTriangles <= 0) continue;
                    var mat = s < compMats.Count && compMats[s] != 0 ? Material(pkg, compMats[s]) : sec.MaterialRef != 0 ? Material(owner, sec.MaterialRef) : null;
                    string matPath = mat?.Path ?? "(no material)";
                    // --skip-material: sections whose material path contains any of these (e.g. water, dev materials).
                    if (skipMaterials is { Length: > 0 } && skipMaterials.Any(k => matPath.Contains(k, StringComparison.OrdinalIgnoreCase))) { skippedSections++; continue; }
                    if (!parts.TryGetValue(matPath, out var part))
                    {
                        var am = new Mesh(TextureExport.SafeName(matPath), PrimitiveType.Triangle);
                        am.UVComponentCount[0] = 2;
                        string diffuse = "";
                        if (mat is { } mm)
                        {
                            string WriteTex(Package tp, int ti, string name)
                            {
                                string texKey = $"{tp.GetHashCode()}:{ti}";
                                if (!texWritten.TryGetValue(texKey, out string? rel))
                                {
                                    rel = Path.Combine(texFolder, TextureExport.SafeName(name) + ".dds");
                                    if (TextureExport.WriteDds(tp, ti, Path.Combine(outDir, rel), out _, cacheFolder) is null) rel = "";
                                    texWritten[texKey] = rel;
                                }
                                return rel;
                            }
                            var notes = new List<string>();
                            foreach (var tx in TextureExport.MaterialTextures(mm.Pkg, mm.Index + 1, notes))
                                if (tx.Parameter.Contains("diffuse", StringComparison.OrdinalIgnoreCase)) { diffuse = WriteTex(mm.Pkg, tx.ExportIndex, tx.Texture); break; }
                            if (diffuse.Length == 0)
                            {
                                // No diffuse parameter (e.g. a vertex-blended terrain Material): write every texture of its
                                // compiled texture list, the first "diff" one as the FBX slot, and list them in a report.
                                var e0 = mm.Pkg.Exports[mm.Index];
                                var layers = new List<string>();
                                try
                                {
                                    foreach (int tr in ExportCopy.MaterialNativeTextures(mm.Pkg, mm.Pkg.ReadExportBytes(e0), mm.Pkg.ClassOf(e0)))
                                    {
                                        if (tr <= 0 || !mm.Pkg.ClassOf(mm.Pkg.Exports[tr - 1]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
                                        string tname = mm.Pkg.PathOf(mm.Pkg.Exports[tr - 1]);
                                        string rel = WriteTex(mm.Pkg, tr - 1, mm.Pkg.Exports[tr - 1].ObjectName);
                                        if (rel.Length == 0) continue;
                                        layers.Add($"  {tname} -> {rel}");
                                        // --diffuse material=texture picks the layer to link; otherwise the first "diff" one.
                                        var pick = diffuseOverride?.FirstOrDefault(o => matPath.Contains(o.Material, StringComparison.OrdinalIgnoreCase));
                                        if (pick is { } pk && pk.Texture != null ? tname.Contains(pk.Texture, StringComparison.OrdinalIgnoreCase)
                                            : diffuse.Length == 0 && tname.Contains("diff", StringComparison.OrdinalIgnoreCase)) diffuse = rel;
                                    }
                                }
                                catch (InvalidDataException) { }
                                if (layers.Count > 0) report.Add($"{matPath}: no diffuse parameter; {layers.Count} texture(s) in its compiled list (blend them by the vertex colours in Blender)" + Environment.NewLine + string.Join(Environment.NewLine, layers));
                            }
                        }
                        parts[matPath] = part = (am, diffuse);
                    }
                    var used = new Dictionary<int, int>();
                    int end = sec.FirstIndex + sec.NumTriangles * 3;
                    for (int i = sec.FirstIndex; i < end; i++)
                    {
                        int v = mesh.Indices[i];
                        if (used.ContainsKey(v)) continue;
                        used[v] = part.Mesh.VertexCount;
                        part.Mesh.Vertices.Add(new Vector3D(w[v].X, w[v].Z, w[v].Y));
                        // Vertex colours (stored B, G, R, A) -> the FBX colour set; white where a mesh has none, so a merged
                        // material mesh keeps one colour channel throughout.
                        var col = mesh.ColorsBgra is { } cb && v * 4 + 3 < cb.Length
                            ? new Color4D(cb[v * 4 + 2] / 255f, cb[v * 4 + 1] / 255f, cb[v * 4] / 255f, cb[v * 4 + 3] / 255f) : new Color4D(1, 1, 1, 1);
                        part.Mesh.VertexColorChannels[0].Add(col);
                        Vector2 uv = mesh.TexCoords[0][v];
                        part.Mesh.TextureCoordinateChannels[0].Add(new Vector3D(uv.X, 1f - uv.Y, 0f));
                    }
                    // Mirror-free transforms keep the engine winding; a negative scale flips it.
                    bool flip = sc.X * sc.Y * sc.Z < 0;
                    for (int i = sec.FirstIndex; i + 2 < end; i += 3)
                        part.Mesh.Faces.Add(flip ? new Face([used[mesh.Indices[i]], used[mesh.Indices[i + 2]], used[mesh.Indices[i + 1]]])
                                                 : new Face([used[mesh.Indices[i]], used[mesh.Indices[i + 1]], used[mesh.Indices[i + 2]]]));
                }
            }
        }
        foreach (var (matPath, part) in parts)
        {
            if (part.Mesh.FaceCount == 0) continue;
            part.Mesh.MaterialIndex = scene.MaterialCount;
            var material = new Assimp.Material { Name = TextureExport.SafeName(matPath) };
            if (part.Diffuse.Length > 0)
                material.AddMaterialTexture(new TextureSlot(part.Diffuse.Replace('\\', '/'), TextureType.Diffuse, 0, TextureMapping.FromUV, 0, 1f, TextureOperation.Add, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0));
            scene.Materials.Add(material);
            scene.Meshes.Add(part.Mesh);
            scene.RootNode.MeshIndices.Add(scene.MeshCount - 1);
        }
        if (report.Count > 0)
            File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(outFbx) + "_layers.txt"), string.Join(Environment.NewLine + Environment.NewLine, report) + Environment.NewLine);
        using var ctx = new AssimpContext();
        if (!ctx.ExportFile(scene, outFbx, "fbx")) throw new IOException($"Assimp could not write {outFbx}");
        if (skippedSections > 0) Console.WriteLine($"  {skippedSections:N0} section(s) left out by --skip-material [{string.Join(", ", skipMaterials!)}]");
        Console.WriteLine($"  {kept:N0} of {placedCount:N0} placed meshes kept; {scene.MeshCount} material(s), {parts.Values.Sum(p => p.Mesh.FaceCount):N0} tris; {texWritten.Values.Count(v => v.Length > 0)} texture(s) in {texFolder}");
        Console.WriteLine($"Wrote {outFbx}");
        return 0;
    }
}
