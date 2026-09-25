using System.Numerics;
using Assimp;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace UpkMeshScan;

/// <summary>
/// Prints what Assimp sees in one or more FBX files: scene metadata (units, axes), node transforms,
/// and per-mesh counts and bounds in both the file's local space and world space. Read-only.
/// For comparing a re-saved FBX against the original export before designing the import.
/// </summary>
static class FbxInspect
{
    public static int Run(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            Console.WriteLine();
            Console.WriteLine($"== {path}");
            if (!File.Exists(path)) { Console.WriteLine("   not found"); continue; }
            Console.WriteLine($"   {new FileInfo(path).Length:N0} bytes");

            using var ctx = new AssimpContext();
            Scene scene;
            // Welding the way an importer would: identical position+normal+UVs become one vertex.
            try { scene = ctx.ImportFile(path, PostProcessSteps.JoinIdenticalVertices); }
            catch (AssimpException ex) { Console.WriteLine($"   Assimp could not read it: {ex.Message}"); continue; }

            if (scene.Metadata.Count > 0)
            {
                Console.WriteLine("   metadata:");
                foreach (var kv in scene.Metadata.OrderBy(k => k.Key))
                    Console.WriteLine($"     {kv.Key} = {kv.Value.Data}");
            }

            Console.WriteLine($"   meshes {scene.MeshCount}, materials {scene.MaterialCount}");
            for (int m = 0; m < scene.MaterialCount; m++)
            {
                Console.WriteLine($"     material[{m}] {scene.Materials[m].Name}");
                foreach (var t in scene.Materials[m].GetAllMaterialTextures())
                    Console.WriteLine($"       {t.TextureType,-9} {t.FilePath}{(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, t.FilePath)) ? "" : "  (FILE NOT FOUND)")}");
            }

            Console.WriteLine("   nodes:");
            Walk(scene, scene.RootNode, Matrix4x4.Identity, 1);
        }
        return 0;
    }

    static void Walk(Scene scene, Node node, Matrix4x4 parent, int depth)
    {
        Matrix4x4 local = ToNumerics(node.Transform);
        Matrix4x4 world = local * parent;
        string pad = new(' ', 3 + depth * 2);
        Console.WriteLine($"{pad}{node.Name}  {Describe(local)}");
        foreach (int mi in node.MeshIndices)
        {
            Mesh mesh = scene.Meshes[mi];
            Vector3 lmin = new(float.MaxValue), lmax = new(float.MinValue), wmin = lmin, wmax = lmax;
            foreach (Vector3D v in mesh.Vertices)
            {
                var p = new Vector3(v.X, v.Y, v.Z);
                lmin = Vector3.Min(lmin, p); lmax = Vector3.Max(lmax, p);
                var w = Vector3.Transform(p, world);
                wmin = Vector3.Min(wmin, w); wmax = Vector3.Max(wmax, w);
            }
            Console.WriteLine($"{pad}  mesh '{mesh.Name}': {mesh.VertexCount:N0} verts, {mesh.FaceCount:N0} faces, material {mesh.MaterialIndex} " +
                              $"({scene.Materials[mesh.MaterialIndex].Name}), normals {mesh.HasNormals}, uv channels {mesh.TextureCoordinateChannelCount}");
            Console.WriteLine($"{pad}    local bounds {F(lmin)} .. {F(lmax)}");
            Console.WriteLine($"{pad}    world bounds {F(wmin)} .. {F(wmax)}");
            for (int c = 0; c < mesh.TextureCoordinateChannelCount; c++)
            {
                var uv = mesh.TextureCoordinateChannels[c];
                if (uv.Count == 0) continue;
                Console.WriteLine($"{pad}    uv{c} range ({uv.Min(t => t.X):0.###}, {uv.Min(t => t.Y):0.###}) .. ({uv.Max(t => t.X):0.###}, {uv.Max(t => t.Y):0.###})");
            }
            int nonTri = mesh.Faces.Count(f => f.IndexCount != 3);
            if (nonTri > 0) Console.WriteLine($"{pad}    {nonTri:N0} non-triangle faces");
        }
        foreach (Node child in node.Children) Walk(scene, child, world, depth + 1);
    }

    static string Describe(Matrix4x4 m)
    {
        if (m.IsIdentity) return "(identity)";
        Matrix4x4.Decompose(m, out Vector3 s, out System.Numerics.Quaternion r, out Vector3 t);
        return $"T {F(t)}  S {F(s)}  R {F(new Vector3(r.X, r.Y, r.Z))} w {r.W:0.####}";
    }

    static string F(Vector3 v) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})";

    // Assimp matrices are row-major with translation in the 4th column; System.Numerics puts it in the 4th row.
    static Matrix4x4 ToNumerics(Assimp.Matrix4x4 m) => Matrix4x4.Transpose(new Matrix4x4(
        m.A1, m.A2, m.A3, m.A4, m.B1, m.B2, m.B3, m.B4, m.C1, m.C2, m.C3, m.C4, m.D1, m.D2, m.D3, m.D4));
}
