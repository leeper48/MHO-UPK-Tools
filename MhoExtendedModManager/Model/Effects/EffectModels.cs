using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Effect models (Kurt, 2026-10-25: Squirrel Girl's falling squirrels in It's Raining Rodents are particles): the static meshes
/// a hero's power effects draw (mesh particles, a projectile's own mesh), changed through the Model tab's Effect Models view.
/// Each one is in several packages under the same path (Squirrel Girl's three squirrel poses are in 12) and the game shows
/// whichever copy it loads first, so an edit is built into every copy. The list is Model\StockData\effect_models.txt
/// (<see cref="Census"/> on the stock packages); the FBX round trip is MHO Package Modifier's static mesh export and import.
/// </summary>
static class EffectModels
{
    /// <summary>One effect model: its hero, the mesh's path (and name), its size, and every package holding a copy.</summary>
    public sealed record Entry(string Hero, string Path, string Name, int Vertices, int Sections, List<string> Packages);

    static List<Entry>? all;

    /// <summary>Every hero's effect models (from the shipped table).</summary>
    public static List<Entry> All => all ??= Load();

    static List<Entry> Load()
    {
        var list = new List<Entry>();
        using var s = typeof(EffectModels).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.effect_models.txt");
        if (s == null) return list;
        using var r = new StreamReader(s);
        for (string? line; (line = r.ReadLine()) != null;)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            if (c.Length < 6 || !int.TryParse(c[3], out int verts) || !int.TryParse(c[4], out int secs)) continue;
            list.Add(new Entry(c[0], c[1], c[1][(c[1].LastIndexOf('.') + 1)..], verts, secs, [.. c[5].Split(';', StringSplitOptions.RemoveEmptyEntries)]));
        }
        return list;
    }

    /// <summary>The package kinds whose static meshes are effects (powers, their hotspots, projectiles and conditions).</summary>
    static readonly string[] Kinds = ["UC__Power", "UC__MarvelEntity_", "UC__MarvelProjectile_", "UC__MarvelConditionEffect_"];

    /// <summary>
    /// Read-only (--effect-census): every StaticMesh export in the game's effect packages, by path, with the packages holding
    /// a copy; a mesh belongs to each hero whose name its packages carry (UC__Power&lt;Hero&gt;_…, a hotspot / projectile /
    /// condition named after the hero). Writes "hero, path, (unused), vertices, sections, packages" lines.
    /// </summary>
    public static int Census(string cooked, string outFile, Action<string> log)
    {
        var heroes = Directory.EnumerateFiles(cooked, "UC__MarvelPlayer_*_SF.upk").Select(f => System.IO.Path.GetFileNameWithoutExtension(f).Split('_', StringSplitOptions.RemoveEmptyEntries))
            .Where(q => q.Length == 4).Select(q => q[2]).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length).ToList();
        var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var firstAt = new Dictionary<string, (string File, int Index)>(StringComparer.OrdinalIgnoreCase);
        int seen = 0;
        foreach (string f in Directory.EnumerateFiles(cooked, "UC__*.upk").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string name = System.IO.Path.GetFileName(f);
            if (name.Contains("bak", StringComparison.OrdinalIgnoreCase) || name.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Kinds.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
            Package p;
            try { p = Package.Open(StockFiles.For(cooked, name)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
            seen++;
            for (int i = 0; i < p.Exports.Length; i++)
            {
                if (!p.ClassOf(p.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) continue;
                string path = p.PathOf(p.Exports[i]);
                if (!byPath.TryGetValue(path, out var files)) { byPath[path] = files = []; firstAt[path] = (name, i); }
                files.Add(name);
            }
        }
        string? HeroOf(string file)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(file);
            foreach (string k in Kinds)
                if (stem.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = stem[k.Length..].TrimStart('_');
                    // a power package starts with its hero (UC__PowerSquirrelGirl_…); the others name it somewhere
                    // (UC__MarvelEntity_Hotspot_SquirrelGirlAoE_SF)
                    return k == "UC__Power" ? heroes.FirstOrDefault(h => rest.StartsWith(h, StringComparison.OrdinalIgnoreCase))
                        : heroes.FirstOrDefault(h => rest.Contains(h, StringComparison.OrdinalIgnoreCase));
                }
            return null;
        }
        var lines = new List<string> { "# Effect models (--effect-census on the stock packages): hero, static mesh path, -, vertices, sections, every package holding a copy (;)" };
        int rows = 0;
        foreach (var (path, files) in byPath.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var owners = files.Select(HeroOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (owners.Count == 0) continue;
            int verts = 0, secs = 0;
            try
            {
                var (f0, i0) = firstAt[path];
                var p0 = Package.Open(StockFiles.For(cooked, f0));
                var sm = StaticMesh.Read(p0, p0.Exports[i0]);
                verts = sm.Positions.Length; secs = sm.Sections.Length;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { continue; }
            if (verts == 0) continue;
            foreach (string h in owners.Order(StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"{h}\t{path}\t-\t{verts}\t{secs}\t{string.Join(";", files)}");
                rows++;
            }
        }
        File.WriteAllLines(outFile, lines);
        log($"{seen:N0} effect packages, {byPath.Count:N0} static meshes, {rows:N0} hero effect models → {outFile}");
        return 0;
    }

    /// <summary>The stock copy of an effect model's first package, for the export and the preview.</summary>
    public static string? StockPackage(Entry e, string cooked) =>
        e.Packages.Select(f => StockFiles.For(cooked, f)).FirstOrDefault(File.Exists);

    /// <summary>The mesh as an FBX for Blender (MHO Package Modifier's static mesh export, with its textures and its own
    /// normals: without them the import's flat normals split every corner, and Venom's 100k-vertex spawner went past the
    /// 16-bit limit): written into
    /// <paramref name="folder"/> as model.fbx (beside it, the textures the FBX names). Returns its path.</summary>
    public static string Export(string packagePath, string path, string folder)
    {
        Protected.CheckWrite(folder);
        Directory.CreateDirectory(folder);
        var console = Console.Out;
        try { Console.SetOut(TextWriter.Null); if (StaticMeshExport.Run(packagePath, path, folder, quiet: true, normals: true) != 0) throw new InvalidDataException($"{path} couldn't be exported from {System.IO.Path.GetFileName(packagePath)}"); }
        finally { Console.SetOut(console); }
        string name = path[(path.LastIndexOf('.') + 1)..];
        string written = System.IO.Path.Combine(folder, name + ".fbx"), model = System.IO.Path.Combine(folder, "model.fbx");
        File.Copy(written, model, true);
        return model;
    }

    /// <summary>Each section's colour texture for the 3D preview (as MHO Package Modifier's Meshes tab picks it: the material's
    /// diffuse parameter, else a texture named like one, else the first that isn't a normal / spec / mask map).</summary>
    public static MhoPackageModifier.Gui.MeshViewer.Tex?[] PreviewTextures(Package pkg, StaticMesh sm, string? cooked)
    {
        var tex = new MhoPackageModifier.Gui.MeshViewer.Tex?[sm.Sections.Length];
        var cache = new Dictionary<int, MhoPackageModifier.Gui.MeshViewer.Tex?>();
        for (int s = 0; s < sm.Sections.Length; s++)
        {
            int mat = sm.Sections[s].MaterialRef;
            if (mat == 0) continue;
            List<MaterialTexture> list;
            try { list = TextureExport.MaterialTextures(pkg, mat, new List<string>()); }
            catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { continue; }
            if (list.Count == 0 && mat > 0)
                try
                {
                    var me = pkg.Exports[mat - 1];
                    foreach (int r in ExportCopy.MaterialNativeTextures(pkg, pkg.ReadExportBytes(me), pkg.ClassOf(me)))
                        if (r > 0 && pkg.ClassOf(pkg.Exports[r - 1]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                            list.Add(new MaterialTexture("", pkg.Exports[r - 1].ObjectName, r - 1));
                }
                catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
            static bool NotColour(string n) => n.Contains("norm", StringComparison.OrdinalIgnoreCase) || n.Contains("spec", StringComparison.OrdinalIgnoreCase)
                || n.Contains("mask", StringComparison.OrdinalIgnoreCase) || n.EndsWith("_n", StringComparison.OrdinalIgnoreCase) || n.Contains("cube", StringComparison.OrdinalIgnoreCase);
            var pick = list.FirstOrDefault(t => t.Parameter.Contains("diffuse", StringComparison.OrdinalIgnoreCase) || t.Parameter.Contains("basecolor", StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(t => t.Texture.Contains("diff", StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(t => !NotColour(t.Texture) && !NotColour(t.Parameter));
            if (pick == null) continue;
            if (!cache.TryGetValue(pick.ExportIndex, out var t))
            {
                t = null;
                try
                {
                    if (TextureExport.ReadBestMip(pkg, pick.ExportIndex, out _, cooked) is { } mip && TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is byte[] px)
                        t = new MhoPackageModifier.Gui.MeshViewer.Tex(px, mip.Width, mip.Height);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException) { }
                cache[pick.ExportIndex] = t;
            }
            tex[s] = t;
        }
        return tex;
    }

    /// <summary>The mesh export in a package by path, or -1.</summary>
    public static int Find(Package p, string path)
    {
        for (int i = 0; i < p.Exports.Length; i++)
            if (p.ClassOf(p.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) && p.PathOf(p.Exports[i]).Equals(path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// The package with the mesh rebuilt from <paramref name="fbx"/> (MHO Package Modifier's import, verified by reading it
    /// back). The FBX's parts go to the mesh's sections by material name (as exported); a model with other materials (a new
    /// one, from another program) goes in by order, or all into the one section most effect meshes have.
    /// </summary>
    public static byte[] Build(string packagePath, string path, string fbx, out StaticMesh built, out List<string> notes)
    {
        notes = [];
        var pkg = Package.Open(packagePath);
        int index = Find(pkg, path);
        if (index < 0) throw new InvalidDataException($"{System.IO.Path.GetFileName(packagePath)} has no static mesh {path}");
        var original = StaticMesh.Read(pkg, pkg.Exports[index]);
        var imported = FbxMeshReader.Read(fbx, original.NumTexCoords, null).Where(s => s.Indices.Count > 0).ToList();
        if (imported.Count == 0) throw new InvalidDataException($"{System.IO.Path.GetFileName(fbx)} has no triangles");
        BuiltMesh mesh;
        // the mesh's sections with triangles (an empty one stays empty: the export leaves it out, Hawkeye's thrust, the shared
        // tubes and spirals have one)
        var used = original.Sections.Select((o, k) => (o, k)).Where(x => x.o.NumTriangles > 0).ToList();
        var per = original.Sections.Select(o => new ImportedSection(o.MaterialName)).ToList();
        bool byName = imported.All(s => used.Any(u => u.o.MaterialName.Equals(s.Material, StringComparison.OrdinalIgnoreCase)))
            && used.All(u => imported.Any(s => s.Material.Equals(u.o.MaterialName, StringComparison.OrdinalIgnoreCase)))
            && used.Select(u => u.o.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == used.Count;
        if (used.Count == 0) throw new InvalidDataException($"{path} has no triangles");
        if (byName) foreach (var (o, k) in used) per[k] = imported.First(s => s.Material.Equals(o.MaterialName, StringComparison.OrdinalIgnoreCase));
        else
        {
            int n = used.Count;
            if (imported.Count < n) throw new InvalidDataException($"the model has {imported.Count} material(s) for the mesh's {n} sections (it needs one per section: {string.Join(", ", used.Select(u => u.o.MaterialName))})");
            // by order; extra materials join the last section
            for (int j = 0; j < n; j++) per[used[j].k] = j < n - 1 ? imported[j] : Merge(used[j].o.MaterialName, imported.Skip(j));
            notes.Add(n == 1 ? $"the model's {imported.Count} material(s) all use the mesh's material ({used[0].o.MaterialName})"
                : $"the model's materials go to the mesh's sections by order ({string.Join(", ", used.Select(u => u.o.MaterialName))})");
        }
        mesh = StaticMeshBuilder.BuildBySection(original, per);
        // vertex colors (the squirrels' run meshes have them; the builder won't drop them): each vertex takes its nearest
        // original vertex's, so an unchanged mesh keeps its own exactly and a reshaped one keeps them where it was
        if (original.HasVertexColors && original.ColorsBgra is { } oc && mesh.ColorsBgra == null)
        {
            var cols = new byte[mesh.Positions.Length * 4];
            for (int v = 0; v < mesh.Positions.Length; v++)
            {
                int best = 0; float bd = float.MaxValue;
                for (int o = 0; o < original.Positions.Length; o++)
                {
                    float d = System.Numerics.Vector3.DistanceSquared(mesh.Positions[v], original.Positions[o]);
                    if (d < bd) { bd = d; best = o; }
                }
                Array.Copy(oc, best * 4, cols, v * 4, 4);
            }
            mesh = new BuiltMesh
            {
                Positions = mesh.Positions, Normals = mesh.Normals, TexCoords = mesh.TexCoords, TangentX = mesh.TangentX, TangentZ = mesh.TangentZ,
                Indices = mesh.Indices, Adjacency = mesh.Adjacency, Sections = mesh.Sections, BoundsOrigin = mesh.BoundsOrigin, BoundsExtent = mesh.BoundsExtent,
                BoundsRadius = mesh.BoundsRadius, ColorsBgra = cols,
            };
            notes.Add("vertex colors kept (each vertex takes its nearest original vertex's)");
        }
        byte[] bytes = MeshImport.ReplaceMesh(pkg, index, mesh, out var problems);
        if (problems.Count > 0) throw new InvalidDataException("the rebuilt package didn't verify: " + string.Join("; ", problems));
        var back = Package.FromBytes(bytes);
        built = StaticMesh.Read(back, back.Exports[index]);
        return bytes;
    }

    static ImportedSection Merge(string material, IEnumerable<ImportedSection> parts)
    {
        var m = new ImportedSection(material);
        foreach (var s in parts)
        {
            int at = m.Positions.Count;
            m.Positions.AddRange(s.Positions); m.Normals.AddRange(s.Normals);
            for (int c = 0; c < m.TexCoords.Length; c++) m.TexCoords[c].AddRange(s.TexCoords[c]);
            m.Indices.AddRange(s.Indices.Select(i => i + at));
        }
        return m;
    }
}
