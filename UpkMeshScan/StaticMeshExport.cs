using System.Numerics;
using Assimp;

namespace UpkMeshScan;

/// <summary>
/// Writes a StaticMesh's LOD 0 to FBX through AssimpNet, one mesh part per section, using the
/// same file-space conventions as AnimExportCli's FbxExporter (already checked in Blender):
/// swap Y/Z, flip V, keep winding as read.
/// </summary>
static class StaticMeshExport
{
    public static int Run(string upkPath, string meshName, string outDir, bool quiet = false)
    {
        Package pkg;
        try { pkg = Package.Open(upkPath); }
        catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IOException)
        {
            Console.WriteLine($"Could not read '{upkPath}': {ex.Message}");
            return 1;
        }

        var matches = pkg.Exports
            .Where(e => pkg.ClassOf(e).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(e).Equals(meshName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) { Console.WriteLine($"No StaticMesh named '{meshName}' in {Path.GetFileName(upkPath)}."); return 1; }
        if (matches.Count > 1) { Console.WriteLine($"'{meshName}' matches {matches.Count} StaticMeshes; pass the full path instead:"); matches.ForEach(e => Console.WriteLine($"  {pkg.PathOf(e)}")); return 1; }

        StaticMesh mesh;
        try { mesh = StaticMesh.Read(pkg, matches[0]); }
        catch (PackageFormatException ex) { Console.WriteLine($"Could not decode: {ex.Message}"); return 1; }

        if (!quiet) Console.WriteLine($"{pkg.PathOf(matches[0])}: InternalVersion {mesh.InternalVersion}, {mesh.LodCount} LOD(s), " +
                          $"{mesh.Positions.Length:N0} verts, {mesh.Indices.Length / 3:N0} tris, {mesh.NumTexCoords} UV channel(s), {mesh.Sections.Length} section(s)");
        if (!quiet)
        {
            foreach (string note in mesh.Notes) Console.WriteLine($"  note: {note}");
            foreach (var s in mesh.Sections)
                Console.WriteLine($"  section: {s.NumTriangles,7:N0} tris  material {s.MaterialName} (ref {s.MaterialRef})");
        }

        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, $"{mesh.Name}.fbx");
        var slots = ExportTextures(pkg, mesh, outDir, quiet);
        Write(mesh, path, slots);
        if (!quiet) Console.WriteLine($"Wrote {path}");
        return 0;
    }

    sealed record SectionTextures(string? Diffuse, string? Normal, string? Specular);

    /// <summary>
    /// Writes each section's material textures (largest mip inside the package) to &lt;mesh&gt;_textures\ and a
    /// &lt;mesh&gt;_textures.txt report. Returns per-section relative paths for the FBX material slots.
    /// </summary>
    static SectionTextures[] ExportTextures(Package pkg, StaticMesh mesh, string outDir, bool quiet)
    {
        var result = new SectionTextures[mesh.Sections.Length];
        string folder = $"{mesh.Name}_textures";
        var report = new System.Text.StringBuilder();
        report.AppendLine($"Textures for {mesh.Name} (largest mip stored inside the package; full-size mips in .tfc files are not read)");
        var written = new Dictionary<int, string>();
        for (int s = 0; s < mesh.Sections.Length; s++)
        {
            var sec = mesh.Sections[s];
            report.AppendLine();
            report.AppendLine($"section{s}: {sec.MaterialName} ({sec.NumTriangles:N0} tris)");
            var notes = new List<string>();
            if (sec.MaterialRef == 0) notes.Add("no material on the mesh section; the placed component supplies it (not followed yet)");
            string? diffuse = null, normal = null, specular = null;
            foreach (var t in sec.MaterialRef == 0 ? new List<MaterialTexture>() : TextureExport.MaterialTextures(pkg, sec.MaterialRef, notes))
            {
                if (!written.TryGetValue(t.ExportIndex, out string? rel))
                {
                    rel = Path.Combine(folder, TextureExport.SafeName(t.Texture) + ".dds");
                    var size = TextureExport.WriteDds(pkg, t.ExportIndex, Path.Combine(outDir, rel), out string note);
                    report.AppendLine($"  {t.Parameter,-28} {t.Texture,-44} {(size is { } z ? $"{z.W}x{z.H}  {note}" : $"not written: {note}")}");
                    if (size is null) { written[t.ExportIndex] = ""; continue; }
                    written[t.ExportIndex] = rel;
                }
                else report.AppendLine($"  {t.Parameter,-28} {t.Texture,-44} (same file as above)");
                if (rel.Length == 0) continue;
                string pn = t.Parameter.ToLowerInvariant();
                if (diffuse == null && pn.Contains("diffuse")) diffuse = rel;
                else if (normal == null && pn.Contains("normal")) normal = rel;
                else if (specular == null && pn.Contains("spec")) specular = rel;
            }
            foreach (string n in notes) report.AppendLine($"  note: {n}");
            result[s] = new SectionTextures(diffuse, normal, specular);
        }
        File.WriteAllText(Path.Combine(outDir, $"{mesh.Name}_textures.txt"), report.ToString());
        if (!quiet) Console.WriteLine($"  textures: {written.Values.Count(v => v.Length > 0)} written to {folder}{Path.DirectorySeparatorChar} (report: {mesh.Name}_textures.txt)");
        return result;
    }

    static void Write(StaticMesh mesh, string path, SectionTextures[]? textures = null)
    {
        var scene = new Scene { RootNode = new Node(mesh.Name) };
        for (int s = 0; s < mesh.Sections.Length; s++)
        {
            var sec = mesh.Sections[s];
            if (sec.NumTriangles <= 0) continue;

            var used = new Dictionary<int, int>();
            var part = new Mesh($"{mesh.Name}_section{s}", PrimitiveType.Triangle);
            int channels = Math.Min(mesh.NumTexCoords, 2);
            for (int c = 0; c < channels; c++) part.UVComponentCount[c] = 2;

            int end = sec.FirstIndex + sec.NumTriangles * 3;
            for (int i = sec.FirstIndex; i < end; i++)
            {
                int v = mesh.Indices[i];
                if (!used.TryAdd(v, part.VertexCount)) continue;
                Vector3 p = ToFileSpace(mesh.Positions[v]), n = ToFileSpace(mesh.Normals[v]);
                part.Vertices.Add(new Vector3D(p.X, p.Y, p.Z));
                part.Normals.Add(new Vector3D(n.X, n.Y, n.Z));
                for (int c = 0; c < channels; c++)
                {
                    Vector2 uv = mesh.TexCoords[c][v];
                    part.TextureCoordinateChannels[c].Add(new Vector3D(uv.X, 1f - uv.Y, 0f));
                }
            }
            for (int i = sec.FirstIndex; i + 2 < end; i += 3)
                part.Faces.Add(new Face([used[mesh.Indices[i]], used[mesh.Indices[i + 1]], used[mesh.Indices[i + 2]]]));

            part.MaterialIndex = scene.MaterialCount;
            var material = new Material { Name = sec.MaterialName };
            if (textures?[s] is { } tx)
            {
                if (tx.Diffuse != null) material.AddMaterialTexture(Slot(tx.Diffuse, TextureType.Diffuse));
                if (tx.Normal != null) material.AddMaterialTexture(Slot(tx.Normal, TextureType.Normals));
                if (tx.Specular != null) material.AddMaterialTexture(Slot(tx.Specular, TextureType.Specular));
            }
            scene.Materials.Add(material);
            scene.Meshes.Add(part);
            scene.RootNode.MeshIndices.Add(scene.MeshCount - 1);
        }
        if (scene.MeshCount == 0) throw new PackageFormatException($"'{mesh.Name}' has no triangles to write");

        using var ctx = new AssimpContext();
        if (!ctx.ExportFile(scene, path, "fbx")) throw new IOException($"Assimp could not write {path}");
    }

    static TextureSlot Slot(string relativePath, TextureType type) =>
        new(relativePath.Replace('\\', '/'), type, 0, TextureMapping.FromUV, 0, 1f, TextureOperation.Add, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0);

    static Vector3 ToFileSpace(Vector3 v) => new(v.X, v.Z, v.Y);
}
