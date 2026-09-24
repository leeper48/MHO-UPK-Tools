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
    public static int Run(string upkPath, string meshName, string outDir)
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

        Console.WriteLine($"{pkg.PathOf(matches[0])}: InternalVersion {mesh.InternalVersion}, {mesh.LodCount} LOD(s), " +
                          $"{mesh.Positions.Length:N0} verts, {mesh.Indices.Length / 3:N0} tris, {mesh.NumTexCoords} UV channel(s), {mesh.Sections.Length} section(s)");
        foreach (string note in mesh.Notes) Console.WriteLine($"  note: {note}");
        foreach (var s in mesh.Sections)
            Console.WriteLine($"  section: {s.NumTriangles,7:N0} tris  material {s.MaterialName}");

        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, $"{mesh.Name}.fbx");
        Write(mesh, path);
        Console.WriteLine($"Wrote {path}");
        return 0;
    }

    static void Write(StaticMesh mesh, string path)
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
            scene.Materials.Add(new Material { Name = sec.MaterialName });
            scene.Meshes.Add(part);
            scene.RootNode.MeshIndices.Add(scene.MeshCount - 1);
        }
        if (scene.MeshCount == 0) throw new PackageFormatException($"'{mesh.Name}' has no triangles to write");

        using var ctx = new AssimpContext();
        if (!ctx.ExportFile(scene, path, "fbx")) throw new IOException($"Assimp could not write {path}");
    }

    static Vector3 ToFileSpace(Vector3 v) => new(v.X, v.Z, v.Y);
}
