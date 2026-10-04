using System.Numerics;
using Assimp;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace MhoMffImporter;

/// <summary>
/// FBX round trip, part 2: re-import (0.11.2). An edited model.fbx (exported by <see cref="FbxExport"/>, cleaned up in Blender
/// and exported again) replaces the retarget's mesh: positions, normals, UVs, triangles and weights come from the file; the
/// skeleton stays the importer's (bone edits in Blender are not taken). The file is lined up with the MHO skeleton by its
/// bones: of the 48 axis swaps / flips, the one (with a uniform scale and offset) that puts the file's bone joints on the
/// importer's, least squares, so Blender's axis and unit settings don't matter. Weights go to MHO bones by name (4 per
/// vertex, normalized). Sections keep their textures by material name (as exported); a new material uses the texture the
/// file names, else the first section's.
/// </summary>
static class FbxReimport
{
    public sealed record Result(int Vertices, int Sections, int BonesMatched, float FitError, List<string> Notes);

    /// <summary>An opened FBX: its scene and every node's global transform (row vectors: p * Global), by name.</summary>
    sealed class Opened(Scene scene, Dictionary<Node, Matrix4x4> globals, Dictionary<string, Node> byName, Dictionary<string, Vector3> bind)
    {
        public Scene Scene = scene; public Dictionary<Node, Matrix4x4> Globals = globals; public Dictionary<string, Node> ByName = byName;
        /// <summary>A bone's joint in the bind pose: from the skin's bind matrices when a mesh is skinned to it (the pose the
        /// mesh was made in), else the node's (which a file exported mid-animation holds posed; Kurt's AmCha_test, 0.11.4).</summary>
        public Vector3? Joint(string bone) => bind.TryGetValue(bone, out var b) ? b : ByName.TryGetValue(bone, out var n) ? Globals[n].Translation : null;
    }

    static Opened Open(string fbx)
    {
        if (!File.Exists(fbx)) throw new FileNotFoundException("no such FBX", fbx);
        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(fbx, PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.LimitBoneWeights);
        if (scene == null || scene.MeshCount == 0) throw new InvalidDataException("the FBX holds no mesh");
        var globals = new Dictionary<Node, Matrix4x4>();
        void Walk(Node n, Matrix4x4 parent)
        {
            var g = MffModel.ToNumerics(n.Transform) * parent;
            globals[n] = g;
            foreach (var c in n.Children) Walk(c, g);
        }
        Walk(scene.RootNode, Matrix4x4.Identity);
        var byName = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in globals.Keys) byName.TryAdd(n.Name, n);
        // bind-pose joints: inverse(OffsetMatrix) is the bone's bind global in mesh space; × the mesh node's global
        var bind = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mesh, node) in MeshNodes(scene))
            foreach (var b in mesh.Bones)
                if (!bind.ContainsKey(b.Name) && Matrix4x4.Invert(MffModel.ToNumerics(b.OffsetMatrix), out var inv))
                    bind[b.Name] = Vector3.Transform(inv.Translation, globals[node]);
        return new Opened(scene, globals, byName, bind);
    }

    /// <summary>The file's meshes (for the parts list).</summary>
    public static List<(string Name, int Vertices)> Meshes(string fbx)
    {
        var o = Open(fbx);
        return MeshNodes(o.Scene).Select(x => (x.Item1.Name, x.Item1.VertexCount)).ToList();
    }

    /// <summary>Re-import: the edited file's mesh replaces the retarget's (the skeleton stays).</summary>
    public static Result Apply(Retargeted r, string fbx, Action<string> log)
    {
        var o = Open(fbx);
        var notes = new List<string>();
        var texOf = new Dictionary<string, Textures>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in r.Sections) { texOf.TryAdd(s.Material, s.Tex); texOf.TryAdd(FbxExport.SafeName(s.Material), s.Tex); }
        var firstTex = r.Sections.Count > 0 ? r.Sections[0].Tex : new Textures();
        var (map, err, matched) = Line(o, r.Bones.Select(b => (b.Name, b.Position)), notes);
        var sections = ReadSections(o, map, r.Bones.Select(b => b.Name).ToList(), null,
            (name, m) => texOf.TryGetValue(name, out var t) ? t : FileTextures(fbx, name, m) ?? firstTex, notes);
        r.Sections.Clear(); r.Sections.AddRange(sections);
        r.HairVerts.Clear(); r.BodyVerts.Clear();
        r.Notes.Add("mesh from " + Path.GetFileName(fbx));
        foreach (var n in notes) log("  " + n);
        return new Result(sections.Sum(s => s.Pos.Length), sections.Count, matched, err, notes);
    }

    /// <summary>
    /// The FBX as the source (0.11.3, Kurt: "MFF or FBX as the source"): its bones give the skeleton's proportions (each MHO
    /// bone the file has sits at the file's joint, with the MHO bone's own orientation; one it lacks keeps its MHO offset from
    /// its parent), its meshes the geometry (<paramref name="meshes"/>: the ones to take, null = all), its materials the
    /// textures (material.png / _sp / _alpha beside it, as Export FBX writes them, else the file's diffuse).
    /// </summary>
    public static Retargeted Load(string fbx, MhoSkeleton sk, IReadOnlyCollection<string>? meshes, Action<string> log)
    {
        var o = Open(fbx);
        var notes = new List<string>();
        var (map, err, matched) = Line(o, Enumerable.Range(0, sk.Bones.Count).Select(i => (sk.Bones[i].Name, sk.Pos(i))), notes);
        var r = new Retargeted { Source = null, Target = sk, Scale = 1 };
        var pos = new Vector3[sk.Bones.Count];
        int fromFile = 0;
        for (int i = 0; i < sk.Bones.Count; i++)
        {
            int parent = sk.Bones[i].ParentIndex == i ? -1 : sk.Bones[i].ParentIndex;
            if (o.Joint(sk.Bones[i].Name) is Vector3 j) { pos[i] = map.Pos(j); fromFile++; }
            else pos[i] = parent >= 0 && parent < i ? pos[parent] + (sk.Pos(i) - sk.Pos(parent)) : sk.Pos(i);
            var g = sk.BoneToModel[i]; g.Translation = pos[i];
            r.Bones.Add(new RefBone { Name = sk.Bones[i].Name, Parent = parent, Global = g, Mapped = o.Joint(sk.Bones[i].Name) != null });
        }
        notes.Add($"skeleton: {fromFile} of {sk.Bones.Count} bones from the file, the rest at their MHO offsets");
        var sections = ReadSections(o, map, r.Bones.Select(b => b.Name).ToList(), meshes,
            (name, m) => FileTextures(fbx, name, m) ?? new Textures(), notes);
        r.Sections.AddRange(sections);
        r.Notes.Add("source: " + fbx);
        foreach (var n in notes) { log("  " + n); r.Notes.Add(n); }
        return r;
    }

    /// <summary>Maps file space into MHO space.</summary>
    sealed record Mapping(Matrix4x4 Axes, float Scale, Vector3 Offset)
    {
        public Vector3 Pos(Vector3 v) => Vector3.Transform(v, Axes) * Scale + Offset;
        public Vector3 Dir(Vector3 v) { var d = Vector3.Transform(v, Axes); return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitZ; }
    }

    /// <summary>Lines the file up with MHO bone positions (by name).</summary>
    static (Mapping, float, int) Line(Opened o, IEnumerable<(string Name, Vector3 Pos)> bones, List<string> notes)
    {
        // core body joints only: cape / hair / prop bones sit where the source put them, not where MHO has them
        var core = new System.Text.RegularExpressions.Regex(@"^g_(pelvis|spine0\d|neck|head|[lr]_(hip|knee|ankle|clavical|shoulder|elbow|wrist))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var all = bones.ToList();
        var pairs = new List<(Vector3 F, Vector3 M)>();
        foreach (var (name, p) in all) if (core.IsMatch(name) && o.Joint(name) is Vector3 j) pairs.Add((j, p));
        if (pairs.Count < 6) { pairs.Clear(); foreach (var (name, p) in all) if (o.Joint(name) is Vector3 j) pairs.Add((j, p)); }
        if (pairs.Count < 4) throw new InvalidDataException($"only {pairs.Count} of the FBX's bones are bones of this MHO skeleton: export it with the armature and MHO bone names (g_...)");
        var (axes, scale, offset, err) = Fit(pairs);
        if (MathF.Abs(scale - 1) < 0.03f && scale != 1)
        {
            // in MHO units already (an Export FBX file): no scale; offset from the centroids
            var f = pairs.Select(q => Vector3.Transform(q.F, axes)).ToList();
            offset = pairs.Aggregate(Vector3.Zero, (acc, q) => acc + q.M) / pairs.Count - f.Aggregate(Vector3.Zero, (acc, v) => acc + v) / f.Count;
            scale = 1;
            // already in MHO model space too (within 5 % of the height of it): no offset either, or the model's own
            // proportions against the base hero's would shift it (America on Captain Marvel: 1.6 units into the floor)
            float height = pairs.Max(q => q.M.Z) - pairs.Min(q => q.M.Z);
            if (offset.Length() < 0.05f * height) offset = Vector3.Zero;
            err = pairs.Select((q, i) => (f[i] + offset - q.M).Length()).Max();
        }
        notes.Add($"lined up by {pairs.Count} bones: scale {scale:0.####}, offset {offset.X:0.00} {offset.Y:0.00} {offset.Z:0.00}, worst bone {err:0.000} units off");
        return (new Mapping(axes, scale, offset), err, pairs.Count);
    }

    static bool IsImage(string f) => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp";

    /// <summary>Textures beside the FBX for a material: name.png (+ _sp, _alpha, _n), else the file's diffuse / normal paths.</summary>
    static Textures? FileTextures(string fbx, string name, Material? m)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(fbx))!;
        string? Beside(string suffix) { string f = Path.Combine(dir, FbxExport.SafeName(name) + suffix + ".png"); return File.Exists(f) ? f : null; }
        string? diffuse = Beside("");
        if (diffuse == null && m != null && m.HasTextureDiffuse && m.TextureDiffuse.FilePath is { Length: > 0 } fp)
        {
            string f = Path.IsPathRooted(fp) ? fp : Path.Combine(dir, fp);
            if (File.Exists(f)) diffuse = f;
        }
        // the model's own normal map (Kurt, 2026-10-04): <material>_n / _normal / _nrm beside it (Export FBX and the rig write
        // _n), else the file the FBX material's normal slot names
        string? normal = Beside("_n") ?? Beside("_normal") ?? Beside("_nrm");
        if (normal == null && m != null && m.HasTextureNormal && m.TextureNormal.FilePath is { Length: > 0 } np)
        {
            string f = Path.IsPathRooted(np) ? np : Path.Combine(dir, np);
            if (File.Exists(f) && IsImage(f)) normal = f;
        }
        return diffuse == null && normal == null ? null : new Textures
        {
            Diffuse = diffuse, Spec = Beside("_sp"), Alpha = Beside("_alpha"), Normal = normal,
            SpecMho = Beside("_mhospec"), SpecColor = Beside("_speccolor"),   // MHO's own packed spec map and spec color (as-is)
        };
    }

    static List<RefSection> ReadSections(Opened o, Mapping map, List<string> boneNames, IReadOnlyCollection<string>? only,
                                         Func<string, Material?, Textures> texFor, List<string> notes)
    {
        var scene = o.Scene;
        int boneMissing = 0;
        var movedToParent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pelvis = boneNames.FindIndex(x => x.Equals("g_pelvis", StringComparison.OrdinalIgnoreCase));
        var sections = new List<RefSection>();
        foreach (var (mesh, node) in MeshNodes(scene))
        {
            if (only != null && !only.Contains(mesh.Name)) continue;
            var g = o.Globals[node];
            int nv = mesh.VertexCount;
            var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv];
            for (int v = 0; v < nv; v++)
            {
                var p = mesh.Vertices[v];
                pos[v] = map.Pos(Vector3.Transform(new Vector3(p.X, p.Y, p.Z), g));
                var n = mesh.HasNormals ? mesh.Normals[v] : new Vector3D(0, 0, 1);
                nrm[v] = map.Dir(Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), g));
                uv[v] = mesh.HasTextureCoords(0) ? new Vector2(mesh.TextureCoordinateChannels[0][v].X, mesh.TextureCoordinateChannels[0][v].Y) : Vector2.Zero;
            }
            var acc = new Dictionary<int, float>[nv];
            for (int v = 0; v < nv; v++) acc[v] = new();
            foreach (var b in mesh.Bones)
            {
                int bi = boneNames.FindIndex(x => x.Equals(b.Name, StringComparison.OrdinalIgnoreCase));
                // a bone this skeleton doesn't have (Angela's ribbons and cloth on Black Widow, 48 bones): its weights go to its
                // nearest parent the skeleton has, as an MFF model's extra bones do (dropped, the ribbons hung off the rest)
                if (bi < 0 && o.ByName.TryGetValue(b.Name, out var bn))
                    for (var pn = bn.Parent; pn != null && bi < 0; pn = pn.Parent)
                        bi = boneNames.FindIndex(x => x.Equals(pn.Name, StringComparison.OrdinalIgnoreCase));
                if (bi >= 0 && !boneNames[bi].Equals(b.Name, StringComparison.OrdinalIgnoreCase) && b.VertexWeightCount > 0) movedToParent.Add(b.Name);
                if (bi < 0) { if (b.VertexWeightCount > 0) boneMissing++; continue; }
                foreach (var w in b.VertexWeights) if (w.Weight > 0 && w.VertexID < nv) acc[w.VertexID][bi] = acc[w.VertexID].GetValueOrDefault(bi) + w.Weight;
            }
            int unweighted = 0;
            var weights = new (int, float)[nv][];
            for (int v = 0; v < nv; v++)
            {
                var top = acc[v].OrderByDescending(x => x.Value).Take(4).ToList();
                float sum = top.Sum(x => x.Value);
                if (sum <= 0) { unweighted++; weights[v] = [(Math.Max(0, pelvis), 1f)]; continue; }
                weights[v] = top.Select(x => (x.Key, x.Value / sum)).ToArray();
            }
            if (unweighted > 0) notes.Add($"{mesh.Name}: {unweighted} vertices had no weights (put on g_pelvis)");
            var tris = new List<int>();
            foreach (var f in mesh.Faces) if (f.IndexCount == 3) tris.AddRange(f.Indices);
            var mat = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < scene.MaterialCount ? scene.Materials[mesh.MaterialIndex] : null;
            string matName = mat?.Name ?? mesh.Name;
            int dot = matName.LastIndexOf('.');
            if (dot > 0 && int.TryParse(matName[(dot + 1)..], out _)) matName = matName[..dot];   // Blender's ".001"
            var tex = texFor(matName, mat);
            if (tex.Diffuse == null) notes.Add($"{mesh.Name}: no texture found for material '{matName}'");
            sections.Add(new RefSection { Material = matName, Tex = tex, Pos = pos, Normal = nrm, Uv = uv, Tris = tris.ToArray(), Weights = weights });
        }
        if (movedToParent.Count > 0) notes.Add($"{movedToParent.Count} weighted bone(s) in the FBX aren't in this skeleton: their weights went to their nearest parent it has (they move with it, not on their own)");
        if (boneMissing > 0) notes.Add($"{boneMissing} weighted bone(s) in the FBX aren't in the MHO skeleton and have no parent in it: their weights were dropped");
        if (sections.Count == 0) throw new InvalidDataException("no mesh of the FBX was picked");
        return sections;
    }

    static IEnumerable<(Assimp.Mesh, Node)> MeshNodes(Scene scene)
    {
        var stack = new Stack<Node>(); stack.Push(scene.RootNode);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            foreach (int i in n.MeshIndices) yield return (scene.Meshes[i], n);
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    /// <summary>The signed axis permutation, uniform scale and offset that best map file points onto MHO points; the worst
    /// residual.</summary>
    static (Matrix4x4 Axes, float Scale, Vector3 Offset, float Err) Fit(List<(Vector3 F, Vector3 M)> pairs)
    {
        var best = (Matrix4x4.Identity, 1f, Vector3.Zero, float.MaxValue);
        int[][] perms = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (var pm in perms)
            for (int sg = 0; sg < 8; sg++)
            {
                var a = new Matrix4x4();
                for (int row = 0; row < 3; row++)   // file axis 'row' → MHO axis pm[row]
                {
                    float sign = ((sg >> row) & 1) == 1 ? -1 : 1;
                    if (pm[row] == 0) { if (row == 0) a.M11 = sign; else if (row == 1) a.M21 = sign; else a.M31 = sign; }
                    if (pm[row] == 1) { if (row == 0) a.M12 = sign; else if (row == 1) a.M22 = sign; else a.M32 = sign; }
                    if (pm[row] == 2) { if (row == 0) a.M13 = sign; else if (row == 1) a.M23 = sign; else a.M33 = sign; }
                }
                a.M44 = 1;
                var f = pairs.Select(p => Vector3.Transform(p.F, a)).ToList();
                var fm = f.Aggregate(Vector3.Zero, (s, v) => s + v) / f.Count;
                var mm = pairs.Aggregate(Vector3.Zero, (s, p) => s + p.M) / pairs.Count;
                float num = 0, den = 0;
                for (int i = 0; i < f.Count; i++) { num += Vector3.Dot(f[i] - fm, pairs[i].M - mm); den += (f[i] - fm).LengthSquared(); }
                if (den <= 1e-12f || num <= 0) continue;
                float s = num / den;
                var t = mm - s * fm;
                float worst = 0;
                for (int i = 0; i < f.Count; i++) worst = MathF.Max(worst, (s * f[i] + t - pairs[i].M).Length());
                if (worst < best.Item4) best = (a, s, t, worst);
            }
        return best;
    }
}
