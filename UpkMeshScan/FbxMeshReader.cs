using System.Numerics;
using Assimp;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace UpkMeshScan;

/// <summary>Triangles for one material, in engine space, with vertices welded.</summary>
public sealed class ImportedSection(string material)
{
    public string Material { get; } = material;
    public List<Vector3> Positions { get; } = new();
    public List<Vector3> Normals { get; } = new();
    public List<Vector2>[] TexCoords { get; } = [new(), new(), new(), new()];
    public List<int> Indices { get; } = new();
}

/// <summary>
/// Reads an FBX for import: bakes every node transform into the vertices (Blender re-exports
/// put a scale 0.01 / 90° X rotation on the object), converts back to engine space with the
/// same Y/Z swap and V flip AnimExportCli and --export-fbx use, keeps winding as read, and
/// groups triangles by material name (Blender reorders material indices, names survive).
/// </summary>
static class FbxMeshReader
{
    public static List<ImportedSection> Read(string path, int uvChannels)
    {
        using var ctx = new AssimpContext();
        Scene scene = ctx.ImportFile(path, PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices);
        var sections = new Dictionary<string, (ImportedSection Section, Dictionary<VertexKey, int> Weld)>(StringComparer.OrdinalIgnoreCase);
        Walk(scene, scene.RootNode, Matrix4x4.Identity, uvChannels, sections);
        return sections.Values.Select(v => v.Section).ToList();
    }

    static void Walk(Scene scene, Node node, Matrix4x4 parent, int uvChannels, Dictionary<string, (ImportedSection, Dictionary<VertexKey, int>)> sections)
    {
        Matrix4x4 world = ToNumerics(node.Transform) * parent;
        Matrix4x4.Invert(world, out Matrix4x4 inverse);
        Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverse);

        foreach (int mi in node.MeshIndices)
        {
            Mesh mesh = scene.Meshes[mi];
            if (mesh.PrimitiveType != PrimitiveType.Triangle && mesh.Faces.Any(f => f.IndexCount != 3))
                throw new InvalidDataException($"mesh '{mesh.Name}' has non-triangle faces after triangulation");
            string material = scene.Materials[mesh.MaterialIndex].Name;
            if (!sections.TryGetValue(material, out var entry))
                sections[material] = entry = (new ImportedSection(material), new Dictionary<VertexKey, int>());
            var (section, weld) = entry;

            var remap = new int[mesh.VertexCount];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                Vector3 p = Vector3.Transform(ToVec(mesh.Vertices[v]), world);
                Vector3 n = mesh.HasNormals ? Vector3.Normalize(Vector3.TransformNormal(ToVec(mesh.Normals[v]), normalMatrix)) : Vector3.UnitY;
                var uv = new Vector2[uvChannels];
                for (int c = 0; c < uvChannels; c++)
                    uv[c] = mesh.HasTextureCoords(c) ? new Vector2(mesh.TextureCoordinateChannels[c][v].X, 1f - mesh.TextureCoordinateChannels[c][v].Y) : Vector2.Zero;

                Vector3 ep = ToEngine(p), en = ToEngine(n);
                var key = new VertexKey(ep, en, uv);
                if (!weld.TryGetValue(key, out int index))
                {
                    index = section.Positions.Count;
                    weld[key] = index;
                    section.Positions.Add(ep);
                    section.Normals.Add(en);
                    for (int c = 0; c < uvChannels; c++) section.TexCoords[c].Add(uv[c]);
                }
                remap[v] = index;
            }
            foreach (Face f in mesh.Faces)
                if (f.IndexCount == 3)
                    section.Indices.AddRange([remap[f.Indices[0]], remap[f.Indices[1]], remap[f.Indices[2]]]);
        }
        foreach (Node child in node.Children) Walk(scene, child, world, uvChannels, sections);
    }

    /// <summary>File space and engine space differ by a Y/Z swap, which is its own inverse.</summary>
    public static Vector3 ToEngine(Vector3 v) => new(v.X, v.Z, v.Y);

    static Vector3 ToVec(Vector3D v) => new(v.X, v.Y, v.Z);

    static Matrix4x4 ToNumerics(Assimp.Matrix4x4 m) => Matrix4x4.Transpose(new Matrix4x4(
        m.A1, m.A2, m.A3, m.A4, m.B1, m.B2, m.B3, m.B4, m.C1, m.C2, m.C3, m.C4, m.D1, m.D2, m.D3, m.D4));

    readonly record struct VertexKey(Vector3 Position, Vector3 Normal, Vector2 Uv0, Vector2 Uv1, Vector2 Uv2, Vector2 Uv3)
    {
        public VertexKey(Vector3 p, Vector3 n, Vector2[] uv)
            : this(p, n, uv.Length > 0 ? uv[0] : default, uv.Length > 1 ? uv[1] : default, uv.Length > 2 ? uv[2] : default, uv.Length > 3 ? uv[3] : default) { }
    }
}
