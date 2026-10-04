using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;
using Assimp;
using AssimpMesh = Assimp.Mesh;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;

namespace MhoMffImporter;

/// <summary>
/// FBX round trip, part 1: export (0.11.0, Kurt: "a little manual cleanup in Blender will be needed"). Writes the retargeted
/// model as the game will get it: the MHO skeleton (root, g_* bones, MHO bind pose, MFF proportions), the weights, UVs and
/// normals in MHO model space, with AnimExportCli's axis convention (engine Z up → file Y up, no scale) so files line up
/// with the MHO meshes Kurt already exports. Files:
/// <list type="bullet">
/// <item>model.fbx: the mesh, one material per section with its colour map (alpha merged) copied beside it as a PNG.</item>
/// <item>anims\&lt;animation&gt;.fbx: one per base-hero animation, written by AnimExportCli's own FbxExporter (vendored,
/// unchanged): the format the MHO Blender add-on's "FBX to Actions" / "Actions to NLA" read. Positions are kept only for
/// the bones the game takes them for (the AnimSet's UseTranslationBoneNames); every other bone keeps its bind position,
/// as in game, so MFF proportions survive.</item>
/// </list>
/// </summary>
static class FbxExport
{
    /// <param name="adjust">Each animation as written (0.16.1: FBX edits in place of the game's, borrowed hair's motion);
    /// null = the game's.</param>
    /// <param name="exact">The filter names whole animations (the middle panel's one-animation export), not name parts.</param>
    public static void Run(Retargeted r, string package, string outDir, IReadOnlyCollection<string> animFilter, Action<string> log,
        Func<MhoAnimRef, BoneAnimation, BoneAnimation>? adjust = null, bool exact = false)
    {
        Protected.CheckWrite(outDir);
        Directory.CreateDirectory(outDir);
        var bones = MhoAnim.FromGlobals(r.Bones.Select(b => (b.Name, b.Parent, b.Global)).ToList());
        var (mesh, lod) = ToMesh(r, bones);

        // the model, with textures
        var texFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int s = 0; s < r.Sections.Count; s++)
        {
            var sec = r.Sections[s];
            if (sec.Tex.Diffuse == null || texFiles.ContainsKey(sec.Material)) continue;
            string png = SafeName(sec.Material) + ".png";
            CopyColour(sec.Tex.Diffuse, sec.Tex.Alpha, Path.Combine(outDir, png));
            // the other maps beside it, so the FBX can be a source again (material choice, alpha)
            if (sec.Tex.Spec != null) File.Copy(sec.Tex.Spec, Path.Combine(outDir, SafeName(sec.Material) + "_sp.png"), true);
            if (sec.Tex.Alpha != null) File.Copy(sec.Tex.Alpha, Path.Combine(outDir, SafeName(sec.Material) + "_alpha.png"), true);
            if (sec.Tex.SpecMho != null)
            {
                // in Angela's layout (another layout is converted: the copy is read back as hers)
                string mhoSrc = sec.Tex.SpecMhoAngela!;
                string mho = Path.Combine(outDir, SafeName(sec.Material) + "_mhospec" + Path.GetExtension(mhoSrc).ToLowerInvariant());
                File.Copy(mhoSrc, mho, true);
                // and as four gray channel images (image editors show a PNG's alpha as transparency: SpecChannels)
                SpecChannels.Split(mho, outDir, SafeName(sec.Material) + "_mhospec");
                File.SetLastWriteTimeUtc(mho, DateTime.UtcNow.AddSeconds(1));   // the combined map counts as newer until a channel is edited
            }
            if (sec.Tex.GlowFile is string gl) File.Copy(gl, Path.Combine(outDir, SafeName(sec.Material) + "_glow" + Path.GetExtension(gl).ToLowerInvariant()), true);   // (read back as its own glow map)
            if (sec.Tex.SpecColor != null) File.Copy(sec.Tex.SpecColor, Path.Combine(outDir, SafeName(sec.Material) + "_speccolor" + Path.GetExtension(sec.Tex.SpecColor).ToLowerInvariant()), true);
            if (sec.Tex.Normal != null)
            {
                string nf = MaterialOverrides.NormalForGame(sec.Tex.Normal, sec.Tex.NormalFlipGreen, outDir, SafeName(sec.Material)), dst = Path.Combine(outDir, SafeName(sec.Material) + "_n.png");
                if (!Path.GetFullPath(nf).Equals(Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) File.Copy(nf, dst, true);
            }
            texFiles[sec.Material] = png;
        }
        string model = Path.Combine(outDir, "model.fbx");
        WriteModel(model, r, mesh, lod, texFiles);
        log($"model: {model} ({lod.Positions.Count:N0} vertices, {r.Sections.Count} sections, {bones.Count} bones, {texFiles.Count} textures)");

        // the animations
        var anims = MhoAnim.For(package, bones.Select(b => b.Name));
        var picked = animFilter.Count == 0 ? anims : anims.Where(a => animFilter.Any(f => exact ? a.Name.Equals(f, StringComparison.OrdinalIgnoreCase) : a.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        string animDir = Path.Combine(outDir, "anims");
        Directory.CreateDirectory(animDir);
        int done = 0;
        foreach (var a in picked)
        {
            var anim = MhoAnim.Load(a);
            if (anim == null) { log($"  {a.Name}: could not be read"); continue; }
            if (adjust != null) anim = adjust(a, anim);
            try { FbxExporter.WriteAnimated(Path.Combine(animDir, SafeName(a.Name) + ".fbx"), mesh, lod, KeepPositions(anim, a.TranslationBones)); done++; }
            catch (Exception ex) { log($"  {a.Name}: {ex.Message}"); }
        }
        log($"animations: {done} of {picked.Count} written to {animDir}");
    }

    /// <summary>
    /// Export FBX with the work as it shows (0.16.1, Kurt): the borrowed hair grafted on (its bones in model.fbx, weighted as
    /// the preview's), the FBX edits (the edited mesh as the model, each replaced animation in place of the game's), and the
    /// hair's motion in every animation (made on the edit, unless the edit animates the hair itself: as the preview and
    /// Build). Re-imported (Single Animation ▾ or as a source), it gives back what was exported. Returns the notes for the log.
    /// </summary>
    /// <summary>Each animation as the FBX edits have it (an edit in place of the game's); positions as the game takes them.</summary>
    public static Func<MhoAnimRef, BoneAnimation, BoneAnimation> EditsAdjust(AnimEdits? edits) => (ar, a) =>
        edits != null && edits.Anims.TryGetValue(ar.Name, out var f) && File.Exists(f)
            ? AnimEdits.ForGame(f, ar.Name, ar.TranslationBones, AnimEdits.GamePositions(a, ar.TranslationBones))
            : AnimEdits.GamePositions(a, ar.TranslationBones);

    public static void Work(MffModel m, IEnumerable<string> picked, bool subdivide, string package, string? mapFile, int hair, AnimEdits? edits,
        string outDir, Action<string> log, IReadOnlyCollection<string>? animFilter = null, bool exact = false, int cape = 0, string? overrides = null)
    {
        var sk = MhoSkeleton.Load(package, null);
        var rigs = new List<BorrowedRig>();
        foreach (var (kind, n) in new[] { (BorrowedRig.Kind.Cape, cape), (BorrowedRig.Kind.Hair, hair) })
        {
            if (n <= 0) continue;
            (sk, var rig) = BorrowedRig.Graft(sk, kind, n, out string note, kind == BorrowedRig.Kind.Hair ? BorrowedRig.HairDrop(m) : 0);
            log((kind == BorrowedRig.Kind.Cape ? "cape: " : "hair: ") + note);
            if (rig != null) rigs.Add(rig);
        }
        var sel = m.Selected(string.Join(",", picked));
        if (subdivide) sel = Subdivision.Apply(sel);
        var r = Retarget.Run(m, sel, sk, mapFile != null ? BoneMapFile.Load(mapFile) : null);
        if (edits?.ModelFbx is string mf && File.Exists(mf)) { log("mesh from the FBX edit: " + Path.GetFileName(mf)); FbxReimport.Apply(r, mf, log); }
        MaterialOverrides.Apply(r, overrides, log);
        var bones = MhoAnim.FromGlobals(r.Bones.Select(b => (b.Name, b.Parent, b.Global)).ToList());
        foreach (var rig in rigs) rig.MeasureBody(bones, r);
        int edited = 0;
        Run(r, package, outDir, animFilter ?? [], log, (ar, a) =>
        {
            bool isEdit = edits != null && edits.Anims.TryGetValue(ar.Name, out var f) && File.Exists(f);
            var x = isEdit ? AnimEdits.ForGame(edits!.Anims[ar.Name], ar.Name, ar.TranslationBones, AnimEdits.GamePositions(a, ar.TranslationBones))
                : AnimEdits.GamePositions(a, ar.TranslationBones);
            if (isEdit) edited++;
            foreach (var rig in rigs)
                if (!bones.Where(b => rig.Bones.IsMatch(b.Name) && sk.Borrowed.Contains(b.Name)).All(b => x.Tracks.ContainsKey(b.Name))) x = rig.Apply(x, bones);
            return x;
        }, exact);
        if (edited > 0) log($"FBX edits: {edited} animation(s) written as edited");
    }

    /// <summary>The retarget as AnimExportCli's mesh types (one LOD, a section per retarget section; engine UVs).</summary>
    internal static (SkeletalMesh, SkeletalMeshLod) ToMesh(Retargeted r, List<MeshBone> bones)
    {
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var infl = new List<VertexInfluence>();
        var idx = new List<int>(); var secs = new List<MeshSection>();
        for (int s = 0; s < r.Sections.Count; s++)
        {
            var sec = r.Sections[s];
            int baseV = pos.Count, baseI = idx.Count;
            pos.AddRange(sec.Pos); nrm.AddRange(sec.Normal);
            uv.AddRange(sec.Uv.Select(u => new Vector2(u.X, 1 - u.Y)));   // engine UVs (FbxExporter flips them back)
            foreach (var w in sec.Weights) infl.Add(new VertexInfluence { Bones = w.Select(x => x.Bone).ToArray(), Weights = w.Select(x => x.Weight).ToArray() });
            foreach (int t in sec.Tris) idx.Add(baseV + t);
            secs.Add(new MeshSection { MaterialIndex = s, BaseIndex = baseI, TriangleCount = sec.Tris.Length / 3 });
        }
        var lod = new SkeletalMeshLod
        {
            Sections = secs, Indices = idx, Positions = pos, Normals = nrm, TexCoords = uv, Influences = infl,
            Chunks = [new MeshChunk { BaseVertexIndex = 0, VertexCount = pos.Count, BoneMap = Enumerable.Range(0, bones.Count).ToArray() }],
        };
        return (new SkeletalMesh { Name = r.Target.Name, Bones = bones, Lods = [lod] }, lod);
    }

    /// <summary>The animation with position keys only for the bones the game takes positions for (all when the set isn't
    /// rotation-only).</summary>
    internal static BoneAnimation KeepPositions(BoneAnimation a, IReadOnlySet<string>? translationBones)
    {
        if (translationBones == null) return a;
        var tracks = a.Tracks.ToDictionary(kv => kv.Key, kv => translationBones.Contains(kv.Key) ? kv.Value
            : new BoneTrack { PositionKeys = [], RotationKeys = kv.Value.RotationKeys });
        return new BoneAnimation { Name = a.Name, DurationSeconds = a.DurationSeconds, Tracks = tracks };
    }

    /// <summary>The model: FbxExporter's scene (same skeleton, axes, skin, winding) plus a material per section with its
    /// colour map.</summary>
    static void WriteModel(string path, Retargeted r, SkeletalMesh mesh, SkeletalMeshLod lod, Dictionary<string, string> tex)
    {
        var scene = new Scene { RootNode = new Node("Scene") };
        var rest = SkeletonPose.Rest(mesh.Bones);
        var nodes = new Node[mesh.Bones.Count];
        for (int b = 0; b < nodes.Length; b++) nodes[b] = new Node(mesh.Bones[b].Name);
        for (int b = 0; b < nodes.Length; b++)
        {
            int parent = mesh.Bones[b].ParentIndex;
            bool isRoot = b == 0 || parent < 0 || parent == b || parent >= nodes.Length;
            var local = isRoot ? rest.BoneToModel[b] : rest.BoneToModel[b] * rest.ModelToBone[parent];
            nodes[b].Transform = ToAssimp(FbxExporter.ToFileSpace(local));
            if (isRoot) scene.RootNode.Children.Add(nodes[b]); else nodes[parent].Children.Add(nodes[b]);
        }
        for (int s = 0; s < lod.Sections.Count; s++)
        {
            var section = lod.Sections[s];
            var used = new Dictionary<int, int>(); var order = new List<int>();
            for (int c = section.BaseIndex; c < section.BaseIndex + section.TriangleCount * 3; c++)
                if (used.TryAdd(lod.Indices[c], order.Count)) order.Add(lod.Indices[c]);
            var part = new AssimpMesh(SafeName(r.Sections[s].Material), PrimitiveType.Triangle);
            foreach (int v in order)
            {
                var p = FbxExporter.ToFileSpace(lod.Positions[v]); var n = FbxExporter.ToFileSpace(lod.Normals[v]);
                part.Vertices.Add(new Vector3D(p.X, p.Y, p.Z));
                part.Normals.Add(new Vector3D(n.X, n.Y, n.Z));
                part.TextureCoordinateChannels[0].Add(new Vector3D(lod.TexCoords[v].X, 1f - lod.TexCoords[v].Y, 0f));
            }
            part.UVComponentCount[0] = 2;
            for (int c = section.BaseIndex; c + 2 < section.BaseIndex + section.TriangleCount * 3; c += 3)
                part.Faces.Add(new Face([used[lod.Indices[c]], used[lod.Indices[c + 1]], used[lod.Indices[c + 2]]]));
            var byBone = new Dictionary<int, Assimp.Bone>();
            for (int i = 0; i < order.Count; i++)
            {
                var inf = lod.Influences[order[i]];
                for (int w = 0; w < inf.Bones.Count; w++)
                {
                    int bone = inf.Bones[w];
                    if (inf.Weights[w] <= 0 || bone < 0 || bone >= mesh.Bones.Count) continue;
                    if (!byBone.TryGetValue(bone, out var e)) byBone[bone] = e = new Assimp.Bone { Name = mesh.Bones[bone].Name, OffsetMatrix = ToAssimp(FbxExporter.ToFileSpace(rest.ModelToBone[bone])) };
                    e.VertexWeights.Add(new VertexWeight(i, inf.Weights[w]));
                }
            }
            for (int b = 0; b < mesh.Bones.Count; b++)
                if (!byBone.ContainsKey(b)) byBone[b] = new Assimp.Bone { Name = mesh.Bones[b].Name, OffsetMatrix = ToAssimp(FbxExporter.ToFileSpace(rest.ModelToBone[b])) };
            foreach (var bone in byBone.OrderBy(kv => kv.Key).Select(kv => kv.Value)) part.Bones.Add(bone);

            var mat = new Material { Name = SafeName(r.Sections[s].Material) };
            if (tex.TryGetValue(r.Sections[s].Material, out var file))
                mat.TextureDiffuse = new TextureSlot(file, TextureType.Diffuse, 0, TextureMapping.FromUV, 0, 1f, TextureOperation.Multiply, TextureWrapMode.Wrap, TextureWrapMode.Wrap, 0);
            part.MaterialIndex = scene.MaterialCount;
            scene.Materials.Add(mat);
            scene.Meshes.Add(part);
            scene.RootNode.MeshIndices.Add(scene.MeshCount - 1);
        }
        using var ctx = new AssimpContext();
        if (!ctx.ExportFile(scene, path, "fbx")) throw new IOException("The model could not be written as FBX.");
    }

    /// <summary>The colour map as a PNG, with the alpha map (if any) merged into its alpha.</summary>
    static void CopyColour(string colour, string? alpha, string outPng)
    {
        if (alpha == null) { File.Copy(colour, outPng, true); return; }
        using var c = new Bitmap(colour);
        using var a = new Bitmap(alpha);
        using var o = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < c.Height; y++)
            for (int x = 0; x < c.Width; x++)
            {
                var px = c.GetPixel(x, y);
                var ap = a.GetPixel(x * a.Width / c.Width, y * a.Height / c.Height);
                o.SetPixel(x, y, Color.FromArgb((ap.R + ap.G + ap.B) / 3, px.R, px.G, px.B));
            }
        o.Save(outPng, ImageFormat.Png);
    }

    public static string SafeName(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));

    static Assimp.Matrix4x4 ToAssimp(Matrix4x4 m) => new(
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44);
}
