using System.Numerics;
using System.Text.RegularExpressions;
using AnimExportCli.Fbx;
using Assimp;
using AssimpMesh = Assimp.Mesh;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace MhoExtendedModManager.Model;

/// <summary>
/// An MFF part as a prop of its own (Kurt, 2026-10-27: Add as a Prop on the Parts list; MFF hangs its weapons on weapon bones,
/// Bone_w, BoneW…, which the importer splits into Prop parts). The part is put on the costume's skeleton by the retarget as a
/// body part would be (so it sits in the hand as MFF holds it, sized to the hero), then written as an FBX in the bone's own
/// frame: the bone that carries most of its weight (Bone_w → the right hand), the prop's origin on that bone. Read back by
/// <see cref="FbxReimport.LoadProp"/> as made (Add a Prop → From an FBX), it is held where MFF holds it. Textures are copied
/// beside the FBX under the names that reader looks for.
/// </summary>
static class MffPropExport
{
    public sealed record Result(string Fbx, string Bone, int Vertices);

    public static Result Write(MffModel m, Part part, MhoSkeleton body, BoneMapFile? map, string outDir, Action<string> log)
    {
        var r = Retarget.Run(m, [InHand(m, part, log)], body, map);
        if (r.Sections.Count == 0 || r.Sections.All(s => s.Tris.Length == 0)) throw new InvalidDataException($"{part.Name} has no triangles to make a prop of");
        // the bone it's held on: most of its weight, else (an unrigged part) the bone nearest its middle
        var weight = new Dictionary<int, float>();
        foreach (var s in r.Sections) foreach (var w in s.Weights) foreach (var (b, x) in w) weight[b] = weight.GetValueOrDefault(b) + x;
        int bone;
        if (weight.Count > 0) bone = weight.MaxBy(kv => kv.Value).Key;
        else
        {
            var mid = r.Sections.SelectMany(s => s.Pos).Aggregate(Vector3.Zero, (a, p) => a + p) / r.Sections.Sum(s => s.Pos.Length);
            bone = Enumerable.Range(0, r.Bones.Count).MinBy(i => (r.Bones[i].Position - mid).LengthSquared());
        }
        string boneName = r.Bones[bone].Name;
        if (!Matrix4x4.Invert(r.Bones[bone].Global, out var toBone)) throw new InvalidDataException($"{boneName}'s rest pose can't be inverted");
        log($"prop:    {part.Name} is held on {boneName} (most of its weight{(r.Bones[bone].From is string f ? $", MFF {f}" : "")})");

        Directory.CreateDirectory(outDir);
        string fbx = Path.Combine(outDir, FbxExport.SafeName(part.Name.Replace(" · ", "_")) + ".fbx");
        var scene = new Scene { RootNode = new Node("Scene") };
        var mats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int verts = 0;
        foreach (var s in r.Sections.Where(s => s.Tris.Length > 0))
        {
            string mat = FbxExport.SafeName(s.Material);
            var mesh = new AssimpMesh(mat, PrimitiveType.Triangle);
            for (int v = 0; v < s.Pos.Length; v++)
            {
                // the bone's frame, written with the app's own FBX axes (FbxReimport.LoadProp reads them back)
                var p = FbxExporter.ToFileSpace(Vector3.Transform(s.Pos[v], toBone));
                var n = FbxExporter.ToFileSpace(Vector3.Normalize(Vector3.TransformNormal(s.Normal[v], toBone)));
                mesh.Vertices.Add(new Vector3D(p.X, p.Y, p.Z));
                mesh.Normals.Add(new Vector3D(n.X, n.Y, n.Z));
                mesh.TextureCoordinateChannels[0].Add(new Vector3D(s.Uv[v].X, s.Uv[v].Y, 0f));
            }
            mesh.UVComponentCount[0] = 2;
            for (int t = 0; t + 2 < s.Tris.Length; t += 3) mesh.Faces.Add(new Face([s.Tris[t], s.Tris[t + 1], s.Tris[t + 2]]));
            verts += s.Pos.Length;
            if (!mats.TryGetValue(mat, out int mi))
            {
                mi = mats[mat] = scene.MaterialCount;
                var material = new Material { Name = mat };
                string? color = Copy(s.Tex.Diffuse, outDir, mat, "");
                Copy(s.Tex.Normal, outDir, mat, "_n"); Copy(s.Tex.Spec, outDir, mat, "_sp"); Copy(s.Tex.Alpha, outDir, mat, "_alpha"); Copy(s.Tex.Glow, outDir, mat, "_glow");
                if (color != null)
                    material.TextureDiffuse = new TextureSlot(Path.GetFileName(color), TextureType.Diffuse, 0, TextureMapping.FromUV, 0, 1f, TextureOperation.Multiply, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0);
                scene.Materials.Add(material);
            }
            mesh.MaterialIndex = mi;
            scene.Meshes.Add(mesh);
            scene.RootNode.MeshIndices.Add(scene.MeshCount - 1);
        }
        using (var ctx = new AssimpContext())
            if (!ctx.ExportFile(scene, fbx, "fbx")) throw new IOException("The prop could not be written as FBX.");
        return new Result(fbx, boneName, verts);
    }

    /// <summary>
    /// MFF parks a hand's weapon away from the hand in its rest pose (measured 2026-10-27: Red She-Hulk's Bone_w 131 cm from her
    /// right hand, on her other side; Kitty Pryde S01's Bone_S_R / _L 100 and 55 cm; Blade S02's BoneW sword on his back), and its
    /// animations bring it in. A weapon on a bone under a hand (or under nothing but the model's root: Blade's BoneW_3, 130 cm
    /// beside him; the hand on its side) is moved so that bone sits on the hand joint, its turn kept, and weighted to the hand.
    /// One worn on the body (Blade's hip guns under Bip001, his back sheath under Spine1) stays where it is.
    /// </summary>
    static Part InHand(MffModel m, Part part, Action<string> log)
    {
        var w = new Dictionary<int, float>();
        foreach (var s in part.Sections) foreach (var vw in s.Weights) foreach (var (b, x) in vw) w[b] = w.GetValueOrDefault(b) + x;
        if (w.Count == 0) return part;
        int wb = w.MaxBy(kv => kv.Value).Key;
        if (m.Bones[wb].Name.StartsWith("Bip", StringComparison.OrdinalIgnoreCase)) return part;   // (on the body's own bones)
        int hand = -1; bool body = false;
        for (int c = m.Bones[wb].Parent; c >= 0 && hand < 0; c = m.Bones[c].Parent)
            if (Regex.IsMatch(m.Bones[c].Name, @"\b[LR] Hand$", RegexOptions.IgnoreCase)) hand = c;
            else if (m.Bones[c].Name.StartsWith("Bip", StringComparison.OrdinalIgnoreCase)) body = true;
        if (hand < 0 && body) return part;   // worn: a holster, a sheath
        if (hand < 0)
        {
            // under the root only: the hand on the side it's parked on (+Y is the character's left)
            var mid = part.Sections.SelectMany(s => s.Pos).Aggregate(Vector3.Zero, (a, p) => a + p) / Math.Max(1, part.Verts);
            string side = mid.Y >= 0 ? "L" : "R";
            hand = m.Bones.FindIndex(b => Regex.IsMatch(b.Name, $@"\b{side} Hand$", RegexOptions.IgnoreCase));
            if (hand < 0) return part;
        }
        var shift = m.Bones[hand].Position - m.Bones[wb].Position;
        log($"prop:    {m.Bones[wb].Name} is parked {shift.Length():0} cm from {m.Bones[hand].Name} in MFF's rest pose: put on the hand (its turn kept: Prop Fit turns it)");
        var moved = new Part { Name = part.Name, IsProp = true, OwnMaterial = part.OwnMaterial };
        foreach (var s in part.Sections)
            moved.Sections.Add(new Section
            {
                Material = s.Material, Tex = s.Tex, Tris = s.Tris, Normal = s.Normal, Uv = s.Uv,
                Pos = s.Pos.Select(p => p + shift).ToArray(),
                Weights = s.Pos.Select(_ => new[] { (hand, 1f) }).ToArray(),
            });
        return moved;
    }

    /// <summary>A map copied beside the FBX as &lt;material&gt;&lt;suffix&gt;.png (the names FbxReimport looks for); null when none.</summary>
    static string? Copy(string? from, string dir, string mat, string suffix)
    {
        if (from == null || !File.Exists(from) || !from.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
        string to = Path.Combine(dir, mat + suffix + ".png");
        File.Copy(from, to, true);
        return to;
    }
}
