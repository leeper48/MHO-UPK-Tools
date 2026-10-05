using System.Numerics;
using System.Text.RegularExpressions;
using Assimp;
using Assimp.Configs;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Material = Assimp.Material;

namespace MhoExtendedModManager.Model;

/// <summary>One mesh of the model with one material (Assimp splits multi-material meshes per material).
/// Positions and normals are in the normalized frame (see <see cref="MffModel"/>).</summary>
sealed class Section
{
    public required string Material;
    public required Vector3[] Pos;
    public required Vector3[] Normal;
    public required Vector2[] Uv;
    public required int[] Tris;
    /// <summary>Per vertex: (index into <see cref="MffModel.Bones"/>, weight), weights as stored (not renormalized).</summary>
    public required (int Bone, float Weight)[][] Weights;
    public required Textures Tex;
    public int TriCount => Tris.Length / 3;
}

/// <summary>A scene object (FBX model node) with its sections. MFF rips carry many optional pieces (alternate weapons,
/// effect cards): <see cref="DefaultOn"/> is the importer's guess of the character's own body.</summary>
sealed class Part
{
    public required string Name;
    public List<Section> Sections { get; } = new();
    public bool Weighted => Sections.Any(s => s.Weights.Any(w => w.Length > 0));
    public int Verts => Sections.Sum(s => s.Pos.Length);
    public int Tris => Sections.Sum(s => s.TriCount);
    public bool OwnMaterial;
    /// <summary>Looks like a weapon, skill prop or effect by its name or materials (wp, weapon, gun, skill, fx, eff...): off by default.</summary>
    public bool IsProp;
    /// <summary>A swap part: hangs (over 80 % of its weight) on extra bones sitting on a Biped joint (Iron Man S01's open-palm
    /// hands on Bone_L/R_hand, at the wrists); MFF shows it instead of the body's own. Off by default.</summary>
    public bool IsAlternate;
    public bool DefaultOn;
    public Vector3 Min, Max;
    /// <summary>How far this part's vertices were from where their weights put them (a mesh bound in another pose than
    /// the shared skeleton), normalized units. Those vertices are stored at the skinned position.</summary>
    public double BindError;
}

sealed class Bone
{
    public required string Name;
    public int Parent = -1;
    /// <summary>Rest pose, normalized frame (row-vector convention: p' = p * Global).</summary>
    public Matrix4x4 Global;
    public bool Deforms;
    /// <summary>The bone's own name in the file when a <see cref="SkeletonProfile"/> renamed it to a Biped name.</summary>
    public string? Original;
    public Vector3 Position => Global.Translation;
}

/// <summary>Texture files of one material: &lt;material&gt;.png is the colour map, &lt;material&gt;_sp.png the MFF specular
/// map; other &lt;material&gt;_* files are listed as extras.</summary>
sealed class Textures
{
    public string? Diffuse, Spec, Alpha;
    /// <summary>A normal map of the model's own (an FBX's normal slot, or &lt;material&gt;_n / _normal / _nrm beside it): used in
    /// place of the one generated from the color map (Kurt, 2026-10-04: "if a normal map exists it should port over").</summary>
    public string? Normal;
    /// <summary>The normal map is OpenGL style (green up): flipped to MHO's DirectX green (set on the Materials tab).</summary>
    public bool NormalFlipGreen;
    /// <summary>The recipe a spec map is generated with when there's none (SpecMapGen; null = Soft).</summary>
    public string? SpecRecipe;
    /// <summary>A spec map in MHO's own packed layout (chbasematerial_v2: R shine, G spec power, B skin mask, A reflectivity;
    /// a stock character's specmult_specpow_skinmask_reflectivity): used as it is, in place of Spec (Kurt, 2026-10-04, Angela:
    /// her shine is set by what each part is, metal / skin / cloth, which no map made from the colors can know).</summary>
    public string? SpecMho;
    /// <summary>A glow (emissive) map of the model's own: a glow color on black (&lt;material&gt;_glow / _emissive / _emit, an FBX's
    /// emissive slot, or the user's file). Kurt, 2026-10-04: glow / emissive.</summary>
    public string? Glow;
    /// <summary>No glow for this material, whatever its maps say (the Materials tab's No Glow).</summary>
    public bool GlowOff;
    /// <summary>The glow map the material is built with: its own, else the one an MHO spec map's glow channel makes; null = none.</summary>
    public string? GlowFile => GlowOff || Diffuse == null && Glow == null ? null
        : Glow ?? (SpecMho != null ? SpecLayouts.GlowMap(SpecMho, SpecLayoutUsed, Diffuse!) : ColorTags is { Count: > 0 } ct ? MhoExtendedModManager.Model.ColorTags.GlowFile(Diffuse!, ct) : null);
    /// <summary>The layout SpecMho is packed in (SpecLayouts id; null = from its file name, else Angela's skin-mask layout).</summary>
    public string? SpecLayout;
    /// <summary>The layout SpecMho is read with.</summary>
    public SpecLayouts.Layout SpecLayoutUsed => SpecLayouts.ById(SpecLayout) ?? (SpecMho != null ? SpecLayouts.FromName(SpecMho) : null) ?? SpecLayouts.All[0];
    /// <summary>SpecMho in Angela's layout (what the preview, Build and Export use): the file, or a converted copy.</summary>
    public string? SpecMhoAngela => SpecMho == null ? null : SpecLayouts.ToAngela(SpecMho, SpecLayoutUsed);
    /// <summary>A spec color map (MHO's speccolortex: the highlight's tint, gold on gold armor).</summary>
    public string? SpecColor;
    /// <summary>The user's color group tags (group color hex → metal / skin / leather / cloth; ColorTags): an MHO spec map is
    /// made from them when there's none of its own.</summary>
    public List<(string Color, string Tag)>? ColorTags;
    /// <summary>The material gets an MHO packed spec map (its own, or made from color tags): the Metal template reads it.</summary>
    public bool UsesMhoSpec => SpecMho != null || ColorTags is { Count: > 0 };
    /// <summary>Found under a near name (the material without the model number, e.g. hero_squirrelgirl_S02_01 for hero_squirrelgirl01_S02_01).</summary>
    public bool Guessed;
    public List<string> Extra { get; } = new();
}

/// <summary>
/// An extracted MFF model read with Assimp, put into one normalized frame:
/// <b>Z up, facing +X, the character's left on +Y</b> (right-handed), feet at Z = 0, centered on the pelvis,
/// in centimeters (MFF files are in meters: <see cref="UnitScale"/>), so characters keep their own sizes
/// (Dazzler 182, Hulk 233). Up and facing come from the Biped bones (head over pelvis, left thigh vs right thigh,
/// toes ahead of the feet); without them, from the bounding box.
/// Phase 2 maps this frame onto the MHO skeleton (whose own frame is checked there).
/// </summary>
sealed class MffModel
{
    /// <summary>File units (meters, checked on 891 Biped models: median 1.91, Hulk 2.33, Dazzler 1.82) to centimeters.
    /// It was a fit to 180 units per model before 0.2.0, which made Gamora S02 (body measured on half its meshes) too big.</summary>
    public const float UnitScale = 100f;

    public required string File;
    public string Folder => Path.GetFileName(Path.GetDirectoryName(File)!)!;
    public List<Part> Parts { get; } = new();
    public List<Bone> Bones { get; } = new();
    public float SourceHeight;        // body height (feet to top of head) in the file's units (meters)
    public string FrameFrom = "";     // "bones" or "bounding box"
    public bool ToesForward;          // check: toes ahead of the feet after normalizing
    public double BindError;          // max |rest-pose skinning − mesh| over all weighted vertices, normalized units
    public int MaxInfluences;
    /// <summary>Share of triangles (selected-by-default parts) whose corner order points the same way as the vertex normals.</summary>
    public float WindingAgree;
    /// <summary>Weighted bones whose bind pose (first mesh) differs from the scene pose by more than 0.1 normalized units (informational).</summary>
    public int MovedFromRest;
    /// <summary>Largest disagreement between meshes about one bone's bind position, normalized units (0 = consistent).</summary>
    public double BindConflict;
    double bindConflictFile;
    readonly List<double> movedFile = new();
    public List<string> Warnings { get; } = new();
    /// <summary>The skeleton family whose bones were renamed to Biped names (<see cref="SkeletonProfile"/>: "Mixamo"); null
    /// for an MFF (Biped) model; "Guessed" when the pairs come from the skeleton's shape (<see cref="SkeletonProfile.Guess"/>).</summary>
    public string? Profile;

    public IEnumerable<Part> Selected(string? spec)
    {
        if (spec is null or "default") return Parts.Where(p => p.DefaultOn);
        if (spec == "all") return Parts;
        var names = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Parts.Where(p => names.Any(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The name the model's own materials start with: folder name up to the model number ("hero_punisher01").</summary>
    static readonly Regex PropName = new(@"(wp\d*|weapon|wepon|gun|skill|_fx|fx_|eff|effect|shield|sword|dummy|drop_box|_web|arrow|(?<!el)bow|quiver|k?nife|dagger|axe|hammer|spear|staff|bike|motorcycle|vehicle)", RegexOptions.IgnoreCase);

    public static string BaseName(string folder)
    {
        var m = Regex.Match(folder, @"^(.*?\d\d)(?:_s\d+.*)?$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : folder;
    }

    // ---------------------------------------------------------------- loading

    public static MffModel Load(string file, TextureIndex? texIndex = null)
    {
        using var ctx = new AssimpContext();
        ctx.SetConfig(new FBXPreservePivotsConfig(false));
        Scene scene;
        try { scene = ctx.ImportFile(file, PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices); }
        catch (AssimpException ex) { throw new InvalidDataException($"Assimp could not read {file}: {ex.Message}"); }

        var model = new MffModel { File = file };
        // another skeleton family (Mixamo …): its bones take the Biped names, so it goes through the same retarget
        var profile = SkeletonProfile.Apply(scene, model.Warnings);
        model.Profile = profile?.Family;
        var originalOf = profile?.Rename.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal) ?? [];
        texIndex ??= new TextureIndex(Path.GetDirectoryName(file)!);

        // Node world transforms (row-vector System.Numerics = transpose of Assimp's column-vector matrices).
        var global = new Dictionary<Node, Matrix4x4>();
        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
        void Walk(Node n, Matrix4x4 parent)
        {
            var g = ToNumerics(n.Transform) * parent;
            global[n] = g;
            byName.TryAdd(n.Name, n);
            foreach (var c in n.Children) Walk(c, g);
        }
        Walk(scene.RootNode, Matrix4x4.Identity);

        // Skeleton: every node a mesh bone names, plus its ancestors below the scene root (Biped parents such as Bip001).
        var deform = new HashSet<string>(scene.Meshes.SelectMany(m => m.Bones).Select(b => b.Name));
        var meshNodes = new HashSet<Node>(global.Keys.Where(n => n.MeshCount > 0));
        var skel = new HashSet<Node>();
        foreach (var name in deform)
            for (var n = byName.GetValueOrDefault(name); n != null && n != scene.RootNode && !meshNodes.Contains(n); n = n.Parent)
                skel.Add(n);
        // The skeleton is the pose the file's scene is in (node transforms), and every weighted vertex is stored where
        // its own weights put it in that pose (vertex * offset * G), as MFF and Blender show the file. Each mesh keeps its
        // own bind (offset matrices), so meshes bound in different poses (Gamora S02: body split in two, bound apart)
        // still line up. Taking the bind of one mesh as the skeleton instead tore such files apart (tested 2026-09-30).
        // The bind poses are only compared here, for the report (BindConflict / MovedFromRest).
        var bind = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        double conflict = 0;
        // Order: the character's own non-prop meshes first (material named after the model), then by size.
        string baseForBind = BaseName(Path.GetFileName(Path.GetDirectoryName(file)!)!);
        int Rank(Node n, int mi)
        {
            string mat = scene.Meshes[mi].MaterialIndex < scene.MaterialCount ? scene.Materials[scene.Meshes[mi].MaterialIndex].Name : "";
            bool own = mat.StartsWith(baseForBind, StringComparison.OrdinalIgnoreCase);
            bool prop = PropName.IsMatch(n.Name) || PropName.IsMatch(mat);
            return own && !prop ? 0 : !prop ? 1 : 2;
        }
        foreach (var (node, mi) in meshNodes.SelectMany(n => n.MeshIndices.Select(i => (n, i)))
                     .OrderBy(x => Rank(x.n, x.i)).ThenByDescending(x => scene.Meshes[x.i].VertexCount))
                foreach (var b in scene.Meshes[mi].Bones)
                {
                    if (!Matrix4x4.Invert(ToNumerics(b.OffsetMatrix), out var inv)) continue;
                    var g = inv * global[node];
                    if (bind.TryGetValue(b.Name, out var first)) conflict = Math.Max(conflict, (first.Translation - g.Translation).Length());
                    else bind[b.Name] = g;
                }
        var boneIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        void AddBones(Node n)
        {
            if (skel.Contains(n) && !boneIndex.ContainsKey(n.Name))
            {
                int parent = n.Parent != null && boneIndex.TryGetValue(n.Parent.Name, out int p) ? p : -1;
                var g = global[n];
                if (bind.TryGetValue(n.Name, out var bg) && (bg.Translation - g.Translation).Length() > 1e-6) model.movedFile.Add((bg.Translation - g.Translation).Length());
                boneIndex[n.Name] = model.Bones.Count;
                model.Bones.Add(new Bone { Name = n.Name, Global = g, Deforms = deform.Contains(n.Name), Parent = parent, Original = originalOf.GetValueOrDefault(n.Name) });
            }
            foreach (var c in n.Children) AddBones(c);
        }
        AddBones(scene.RootNode);
        model.bindConflictFile = conflict;

        // Parts and sections, still in the file's world frame.
        string baseName = BaseName(model.Folder);
        var ownSheet = new Regex("^" + Regex.Escape(Regex.Replace(baseName, @"\d\d$", "")) + @"\d\d(?:_|$)", RegexOptions.IgnoreCase);
        double bindErr = 0;
        foreach (var node in global.Keys.Where(n => n.MeshCount > 0))
        {
            var part = new Part { Name = node.Name };
            double partErr = 0;
            var mg = global[node];
            Matrix4x4.Invert(mg, out var mgInv);
            var mgNormal = Matrix4x4.Transpose(mgInv);
            foreach (int mi in node.MeshIndices)
            {
                var mesh = scene.Meshes[mi];
                int nv = mesh.VertexCount;
                var pos = new Vector3[nv]; var nor = new Vector3[nv]; var uv = new Vector2[nv];
                var weights = new List<(int, float)>[nv];
                for (int i = 0; i < nv; i++)
                {
                    var v = mesh.Vertices[i];
                    pos[i] = Vector3.Transform(new Vector3(v.X, v.Y, v.Z), mg);
                    if (mesh.HasNormals) { var n = mesh.Normals[i]; nor[i] = Vector3.Normalize(Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), mgNormal)); }
                    if (mesh.HasTextureCoords(0)) { var t = mesh.TextureCoordinateChannels[0][i]; uv[i] = new Vector2(t.X, t.Y); }
                    weights[i] = new();
                }
                var skinned = new Vector3[nv];
                var skinnedN = new Vector3[nv];
                foreach (var b in mesh.Bones)
                {
                    if (!boneIndex.TryGetValue(b.Name, out int bi)) continue;
                    // Rest-pose check: vertex (mesh space) * offset * bone world should land on the vertex's world position.
                    var skin = ToNumerics(b.OffsetMatrix) * model.Bones[bi].Global;
                    foreach (var w in b.VertexWeights)
                    {
                        if (w.Weight <= 0) continue;
                        weights[w.VertexID].Add((bi, w.Weight));
                        var v = mesh.Vertices[w.VertexID];
                        skinned[w.VertexID] += w.Weight * Vector3.Transform(new Vector3(v.X, v.Y, v.Z), skin);
                        if (mesh.HasNormals) { var nn = mesh.Normals[w.VertexID]; skinnedN[w.VertexID] += w.Weight * Vector3.TransformNormal(new Vector3(nn.X, nn.Y, nn.Z), skin); }
                    }
                }
                for (int i = 0; i < nv; i++)
                {
                    if (weights[i].Count == 0) continue;
                    float sum = weights[i].Sum(x => x.Item2);
                    var at = skinned[i] / sum;
                    partErr = Math.Max(partErr, (at - pos[i]).Length());
                    model.MaxInfluences = Math.Max(model.MaxInfluences, weights[i].Count);
                    // A mesh bound in another pose than the shared skeleton: move the vertex (and turn its normal) to
                    // where its weights put it, as the game does. Consistent files are unchanged (partErr ~ 0).
                    if ((at - pos[i]).LengthSquared() > 0)
                    {
                        pos[i] = at;
                        if (mesh.HasNormals) nor[i] = Vector3.Normalize(skinnedN[i]);
                    }
                }
                var tris = new List<int>(mesh.FaceCount * 3);
                foreach (var f in mesh.Faces) if (f.IndexCount == 3) tris.AddRange(f.Indices);
                string mat = mesh.MaterialIndex < scene.MaterialCount ? scene.Materials[mesh.MaterialIndex].Name : "";
                var tex = texIndex.Find(mat, scene.MaterialCount > mesh.MaterialIndex ? scene.Materials[mesh.MaterialIndex] : null);
                part.Sections.Add(new Section
                {
                    Material = mat, Pos = pos, Normal = nor, Uv = uv, Tris = tris.ToArray(),
                    Weights = weights.Select(w => w.ToArray()).ToArray(), Tex = tex,
                });
                // The model's own sheets: hero_gamora01…, and the same character's other numbered sheets (Gamora S02's body is
                // on hero_gamora02; 8 model folders ship such a 02 sheet).
                if (mat.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) || ownSheet.IsMatch(mat)) part.OwnMaterial = true;
            }
            part.BindError = partErr;
            bindErr = Math.Max(bindErr, partErr);
            if (part.Sections.Count > 0) model.Parts.Add(part);
        }

        // Default selection: the weighted parts in the model's own materials; else every weighted part; else everything.
        foreach (var p in model.Parts) p.IsProp = PropName.IsMatch(p.Name) || p.Sections.All(x => PropName.IsMatch(x.Material));
        // Untextured body parts (0.10.9; Black Panther, Spider-Man, Nova: every FBX material is "DefaultMaterial"): the
        // character's own sheets by name (<hero>, <hero>01, hero_<hero>01, then _02, _03 …), the first untextured part
        // the first sheet, the next the next (the last one repeats). Marked as guessed.
        {
            // only materials without a name of their own (a named one that has no file is another sheet's layout)
            static bool Unnamed(Section x) => x.Tex.Diffuse == null && (x.Material.Length == 0 || x.Material.Equals("DefaultMaterial", StringComparison.OrdinalIgnoreCase));
            var sheets = texIndex.HeroSheets(model.Folder);
            int k = 0;
            if (sheets.Count > 0)
                foreach (var p in model.Parts.Where(p => !p.IsProp && p.Weighted && p.Sections.Any(Unnamed)))
                {
                    var (d, sp, al) = sheets[Math.Min(k++, sheets.Count - 1)];
                    foreach (var x in p.Sections.Where(Unnamed))
                    { x.Tex.Diffuse = d; x.Tex.Spec ??= sp; x.Tex.Alpha ??= al; x.Tex.Guessed = true; }
                }
        }
        model.SplitPropBones();
        // Swap parts (0.7.2, Kurt: Iron Man S01 had two hands per wrist): extra non-Biped bones within 0.5 cm of a Biped bone.
        // Positions are still in the file's units (metres) here: 0.5 / UnitScale (0.10.17; 0.5 read as half a metre, and
        // Spider-Woman S01's hair, on hair bones 11-25 cm from her head, was taken for a swap part and left out).
        float near = 0.5f / UnitScale;
        var dupBone = Enumerable.Range(0, model.Bones.Count).Where(i => !model.Bones[i].Name.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase)
            && model.Bones.Any(o => o.Name.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase) && Vector3.Distance(o.Position, model.Bones[i].Position) < near)).ToHashSet();
        foreach (var p in model.Parts.Where(p => p.Weighted))
        {
            float on = 0, all = 0;
            foreach (var sec in p.Sections) foreach (var vw in sec.Weights) foreach (var w in vw) { all += w.Weight; if (dupBone.Contains(w.Bone)) on += w.Weight; }
            p.IsAlternate = all > 0 && on / all > 0.8f;
        }
        // Effect pieces (0.10.9; Hawkeye's ultimate wire, 20 vertices, 10 m long, in his own S05 sheet, was his only default
        // part): a part reaching over 4 times as far as the Biped skeleton is a prop (2.5 dropped Angel's wings and Medusa's hair), not the body.
        {
            float Extent(Part p)
            {
                var all = p.Sections.SelectMany(x => x.Pos).ToList();
                if (all.Count == 0) return 0;
                var lo = all.Aggregate(Vector3.Min); var hi = all.Aggregate(Vector3.Max); var d = hi - lo;
                return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
            }
            // the yardstick is the Biped skeleton's extent (by part size, Punisher S07's head was the "largest" part)
            var bip = model.Bones.Where(b => b.Name.StartsWith("Bip001 ", StringComparison.OrdinalIgnoreCase)).Select(b => b.Position).ToList();
            if (bip.Count > 4)
            {
                var d = bip.Aggregate(Vector3.Max) - bip.Aggregate(Vector3.Min);
                float body = MathF.Max(d.X, MathF.Max(d.Y, d.Z));
                foreach (var p in model.Parts) if (body > 0 && Extent(p) > 4f * body) p.IsProp = true;
            }
        }
        foreach (var p in model.Parts) p.DefaultOn = p.Weighted && p.OwnMaterial && !p.IsProp && !p.IsAlternate;
        if (!model.Parts.Any(p => p.DefaultOn)) foreach (var p in model.Parts) p.DefaultOn = p.Weighted && !p.IsProp && !p.IsAlternate;
        if (!model.Parts.Any(p => p.DefaultOn)) foreach (var p in model.Parts) p.DefaultOn = p.Weighted && !p.IsAlternate;
        if (!model.Parts.Any(p => p.DefaultOn)) foreach (var p in model.Parts) p.DefaultOn = true;

        model.Normalize(bindErr);
        return model;
    }

    // ---------------------------------------------------------------- normalizing

    Bone? FindBone(params string[] names) =>
        names.Select(n => Bones.FirstOrDefault(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(b => b != null);

    void Normalize(double bindErr)
    {
        var body = Parts.Where(p => p.DefaultOn).SelectMany(p => p.Sections).SelectMany(s => s.Pos).ToList();
        if (body.Count == 0) body = Parts.SelectMany(p => p.Sections).SelectMany(s => s.Pos).ToList();
        if (body.Count == 0) { Warnings.Add("no geometry"); return; }

        var head = FindBone("Bip001 Head");
        var pelvis = FindBone("Bip001 Pelvis", "Bip001");
        var lThigh = FindBone("Bip001 L Thigh"); var rThigh = FindBone("Bip001 R Thigh");
        Vector3 up, left;
        if (head != null && pelvis != null && lThigh != null && rThigh != null)
        {
            FrameFrom = "bones";
            up = Vector3.Normalize(head.Position - pelvis.Position);
            left = lThigh.Position - rThigh.Position;
            left = Vector3.Normalize(left - Vector3.Dot(left, up) * up);
        }
        else
        {
            FrameFrom = "bounding box";
            Warnings.Add("no Biped bones: frame guessed from the bounding box (tallest axis up, facing unknown)");
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var p in body) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var ext = mx - mn;
            up = ext.X >= ext.Y && ext.X >= ext.Z ? Vector3.UnitX : ext.Y >= ext.Z ? Vector3.UnitY : Vector3.UnitZ;
            left = up == Vector3.UnitZ ? Vector3.UnitY : Vector3.UnitZ;
        }
        // Snap to the nearest file axis when within 5° (rips are axis-aligned; keeps the result exact).
        up = Snap(up); left = Snap(left);
        var fwd = Vector3.Normalize(Vector3.Cross(left, up));
        left = Vector3.Cross(up, fwd);

        // Rows of R map the file frame onto (forward, left, up).
        var rot = new Matrix4x4(fwd.X, left.X, up.X, 0, fwd.Y, left.Y, up.Y, 0, fwd.Z, left.Z, up.Z, 0, 0, 0, 0, 1);
        // Floor and top from what the foot and head bones drive (any non-prop part: a body split over several meshes,
        // Gamora S02, still measures whole; skill props such as Groot's vines don't count). Fallback: the body's extent.
        float minUp = body.Min(p => Vector3.Dot(p, up)), maxUp = body.Max(p => Vector3.Dot(p, up));
        var feet = Subtree("Bip001 L Foot", "Bip001 R Foot");
        var headSet = Subtree("Bip001 Head");
        if (feet.Count > 0 && headSet.Count > 0)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var s in Parts.Where(p => p.Weighted && !p.IsProp).SelectMany(p => p.Sections))
                for (int i = 0; i < s.Pos.Length; i++)
                {
                    if (s.Weights[i].Length == 0) continue;
                    int dom = s.Weights[i].MaxBy(w => w.Weight).Bone;
                    float h = Vector3.Dot(s.Pos[i], up);
                    if (feet.Contains(dom)) lo = MathF.Min(lo, h);
                    if (headSet.Contains(dom)) hi = MathF.Max(hi, h);
                }
            if (lo < float.MaxValue) minUp = lo;
            if (hi > float.MinValue && hi > minUp) maxUp = hi;
        }
        SourceHeight = maxUp - minUp;
        const float scale = UnitScale;
        var centre = pelvis?.Position ?? body.Aggregate(Vector3.Zero, (a, b) => a + b) / body.Count;
        var c = Vector3.Transform(centre, rot);
        var m = rot * Matrix4x4.CreateTranslation(-c.X, -c.Y, -minUp) * Matrix4x4.CreateScale(scale);
        var rotOnly = rot;

        foreach (var s in Parts.SelectMany(p => p.Sections))
        {
            for (int i = 0; i < s.Pos.Length; i++) s.Pos[i] = Vector3.Transform(s.Pos[i], m);
            for (int i = 0; i < s.Normal.Length; i++) s.Normal[i] = Vector3.TransformNormal(s.Normal[i], rotOnly);
        }
        foreach (var p in Parts)
        {
            p.Min = new Vector3(float.MaxValue); p.Max = new Vector3(float.MinValue);
            foreach (var v in p.Sections.SelectMany(s => s.Pos)) { p.Min = Vector3.Min(p.Min, v); p.Max = Vector3.Max(p.Max, v); }
        }
        foreach (var b in Bones)
        {
            // Rotation part: file axes → normalized axes; translation through m. Scale is kept out of the bone axes.
            var g = b.Global;
            var t = Vector3.Transform(g.Translation, m);
            var r = g; r.Translation = Vector3.Zero;
            var axes = r * rotOnly;
            // Remove the file's own scale (0.01 armatures) from the axes so bones are rotation + translation.
            var ax = Vector3.Normalize(new Vector3(axes.M11, axes.M12, axes.M13));
            var ay = Vector3.Normalize(new Vector3(axes.M21, axes.M22, axes.M23));
            var az = Vector3.Normalize(new Vector3(axes.M31, axes.M32, axes.M33));
            b.Global = new Matrix4x4(ax.X, ax.Y, ax.Z, 0, ay.X, ay.Y, ay.Z, 0, az.X, az.Y, az.Z, 0, t.X, t.Y, t.Z, 1);
        }
        BindError = bindErr * scale;
        BindConflict = bindConflictFile * scale;
        MovedFromRest = movedFile.Count(d => d * scale > 0.1);
        foreach (var p in Parts) p.BindError *= scale;

        {
            int agree = 0, n = 0;
            foreach (var sec in Parts.Where(p => p.DefaultOn).SelectMany(p => p.Sections))
                for (int t = 0; t + 2 < sec.Tris.Length; t += 3)
                {
                    var a = sec.Pos[sec.Tris[t]]; var b = sec.Pos[sec.Tris[t + 1]]; var cc = sec.Pos[sec.Tris[t + 2]];
                    var g = Vector3.Cross(b - a, cc - a);
                    if (g.LengthSquared() < 1e-12f) continue;
                    var nn = sec.Normal[sec.Tris[t]] + sec.Normal[sec.Tris[t + 1]] + sec.Normal[sec.Tris[t + 2]];
                    n++; if (Vector3.Dot(g, nn) > 0) agree++;
                }
            WindingAgree = n > 0 ? (float)agree / n : 0;
        }
        var lFoot = FindBone("Bip001 L Foot"); var lToe = FindBone("Bip001 L Toe0");
        ToesForward = lFoot != null && lToe != null && lToe.Position.X > lFoot.Position.X;
        if (FrameFrom == "bones" && !ToesForward) Warnings.Add("toes are not ahead of the feet after normalizing: facing may be wrong");
        if (BindError > 0.5) Warnings.Add($"some meshes were bound in another pose: vertices moved by up to {BindError:0.00} cm to follow the shared skeleton");
    }

    /// <summary>
    /// Some rips carry a weapon inside the body mesh (Kate Bishop's bow is in v280_kate bishop_1, weighted to
    /// "weapon Bone002"), which no part filter can remove. Triangles whose three corners are all mostly driven by
    /// prop-named bones (weapon, arrow, gun, knife …) move to their own part "&lt;part&gt; · &lt;bone&gt;", marked a prop:
    /// off by default, and the parts picker can put them back. Vertices are copied, so the body keeps its own.
    /// </summary>
    void SplitPropBones()
    {
        // A prop-named bone and everything below it (Kate's bow tips hang on Bone029, under Dummy001).
        var propBone = Bones.Select(b => PropName.IsMatch(b.Name)).ToArray();
        for (int i = 0; i < Bones.Count; i++) if (Bones[i].Parent >= 0 && propBone[Bones[i].Parent]) propBone[i] = true;
        if (!propBone.Any(x => x)) return;
        foreach (var part in Parts.Where(p => !p.IsProp).ToList())
        {
            var byBone = new Dictionary<int, Part>();
            foreach (var s in part.Sections.ToList())
            {
                int Dom(int v) => s.Weights[v].Length == 0 ? -1 : s.Weights[v].MaxBy(w => w.Weight).Bone;
                var keep = new List<int>(); var moved = new Dictionary<int, List<int>>();
                for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                {
                    int a = Dom(s.Tris[t]), b = Dom(s.Tris[t + 1]), c = Dom(s.Tris[t + 2]);
                    bool prop = a >= 0 && b >= 0 && c >= 0 && propBone[a] && propBone[b] && propBone[c];
                    var list = prop ? (moved.TryGetValue(a, out var l) ? l : moved[a] = new List<int>()) : keep;
                    list.Add(s.Tris[t]); list.Add(s.Tris[t + 1]); list.Add(s.Tris[t + 2]);
                }
                if (moved.Count == 0) continue;
                part.Sections.Remove(s);
                if (keep.Count > 0) part.Sections.Add(Subset(s, keep));
                foreach (var (bone, tris) in moved)
                {
                    if (!byBone.TryGetValue(bone, out var pp))
                        byBone[bone] = pp = new Part { Name = $"{part.Name} · {Bones[bone].Name}", IsProp = true, OwnMaterial = part.OwnMaterial };
                    pp.Sections.Add(Subset(s, tris));
                }
            }
            foreach (var pp in byBone.Values)
            {
                pp.Min = new Vector3(float.MaxValue); pp.Max = new Vector3(float.MinValue);
                Parts.Add(pp);
            }
            if (part.Sections.Count == 0) Parts.Remove(part);
        }
    }

    /// <summary>A section with only the given triangles (vertices reindexed, unused ones dropped).</summary>
    static Section Subset(Section s, List<int> tris)
    {
        var map = new Dictionary<int, int>(); var order = new List<int>();
        var idx = tris.Select(v => { if (!map.TryGetValue(v, out int n)) { map[v] = n = order.Count; order.Add(v); } return n; }).ToArray();
        return new Section
        {
            Material = s.Material, Tex = s.Tex, Tris = idx,
            Pos = order.Select(v => s.Pos[v]).ToArray(), Normal = order.Select(v => s.Normal[v]).ToArray(),
            Uv = order.Select(v => s.Uv[v]).ToArray(), Weights = order.Select(v => s.Weights[v]).ToArray(),
        };
    }

    HashSet<int> Subtree(params string[] roots)
    {
        var set = new HashSet<int>();
        foreach (var r in roots)
        {
            int i = Bones.FindIndex(b => string.Equals(b.Name, r, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) set.Add(i);
        }
        for (bool grew = true; grew;)
        {
            grew = false;
            for (int i = 0; i < Bones.Count; i++)
                if (Bones[i].Parent >= 0 && set.Contains(Bones[i].Parent) && set.Add(i)) grew = true;
        }
        return set;
    }

    static Vector3 Snap(Vector3 v)
    {
        foreach (var a in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            float d = Vector3.Dot(v, a);
            if (MathF.Abs(d) > MathF.Cos(5 * MathF.PI / 180)) return a * MathF.Sign(d);
        }
        return v;
    }

    /// <summary>Assimp matrices are column-vector (translation in A4 B4 C4); System.Numerics is row-vector: transpose.</summary>
    public static Matrix4x4 ToNumerics(Assimp.Matrix4x4 a) => new(
        a.A1, a.B1, a.C1, a.D1,
        a.A2, a.B2, a.C2, a.D2,
        a.A3, a.B3, a.C3, a.D3,
        a.A4, a.B4, a.C4, a.D4);
}

/// <summary>Finds a material's textures: the model's own folder first, then Texture2D (file names compared without case).
/// Falls back to the texture file names stored in the FBX material.</summary>
sealed class TextureIndex
{
    static Dictionary<string, string>? shared;
    static readonly object gate = new();
    readonly Dictionary<string, string> local;

    public TextureIndex(string modelFolder)
    {
        // the folder and two levels below it (rips from elsewhere keep their textures in subfolders: Captain Carter's
        // "Marvel Strike Force Captain Carter\Captain Carter\Char_CaptainCarter_D.png"); the folder's own file first
        local = Directory.EnumerateFiles(modelFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
            .Where(p => ImageExt.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .GroupBy(p => Path.GetFileName(p).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Length).First());
        bool mff;
        try { mff = Directory.Exists(Source.Textures); } catch (InvalidOperationException) { mff = false; }   // no MFF folder set: this folder only
        if (!mff) { sharedHere = new(); nearHere = new(); return; }
        lock (gate)
            shared ??= Directory.EnumerateFiles(Source.Textures, "*.png").GroupBy(p => Path.GetFileName(p).ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        lock (gate)
            near ??= shared!.Keys.Where(k => !Regex.IsMatch(k, @"_(sp|alpha)\.png$")).Select(k => k[..^4])
                .GroupBy(Near).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
    }

    static Dictionary<string, string>? near;
    /// <summary>Without an MFF folder: empty (not cached, so setting the folder later still finds its textures).</summary>
    Dictionary<string, string>? sharedHere, nearHere;
    Dictionary<string, string> Shared => sharedHere ?? shared!;
    Dictionary<string, string> NearNames => nearHere ?? near!;
    /// <summary>Name without the model number after the character name: hero_squirrelgirl01_s02_01 becomes hero_squirrelgirl_s02_01.</summary>
    static string Near(string name) => Regex.Replace(name.ToLowerInvariant(), @"(?<=[a-z])0?1(?=_|$)", "");

    string? Get(string file) => local.GetValueOrDefault(file.ToLowerInvariant()) ?? Shared.GetValueOrDefault(file.ToLowerInvariant());

    /// <summary>The character's own sheets for a model folder (hero_blackpanther01[_S02] → blackpanther, blackpanther01,
    /// hero_blackpanther01, then numbered _02, _03 …): (diffuse, spec, alpha), in that order.</summary>
    public List<(string, string?, string?)> HeroSheets(string folder)
    {
        var mt = Regex.Match(folder.ToLowerInvariant(), @"^(?:hero_)?([a-z]+?)0?1(?:_s(\d+))?$");
        var list = new List<(string, string?, string?)>();
        if (!mt.Success) return list;
        string h = mt.Groups[1].Value;
        if (mt.Groups[2].Success)
        {
            // a costume (…01_S02): only that costume's sheets; no guess from the base costume's
            string nn = mt.Groups[2].Value;
            var pre = new[] { $"hero_{h}01_s{nn}", $"hero_{h}_s{nn}", $"{h}_s{nn}", $"{h}01_s{nn}" };
            foreach (var k in local.Keys.Concat(Shared.Keys).Distinct().OrderBy(k => k))
                if (pre.Any(k.StartsWith) && !Regex.IsMatch(k, @"_(sp|alpha|a|eff|fx|mask)\.png$") && Get(k) is string d && !list.Any(x => x.Item1 == d))
                    list.Add((d, Get(k[..^4] + "_sp.png"), Get(k[..^4] + "_alpha.png")));
            return list;
        }
        var names = new List<string> { h, h + "01", "hero_" + h + "01", h + "1" };
        for (int n = 2; n <= 5; n++) { names.Add($"{h}_0{n}"); names.Add($"{h}0{n}"); names.Add($"hero_{h}0{n}"); names.Add($"{h}01_0{n}"); }
        foreach (var nm in names)
            if (Get(nm + ".png") is string d && !list.Any(x => x.Item1 == d))
                list.Add((d, Get(nm + "_sp.png"), Get(nm + "_alpha.png")));
        return list;
    }

    /// <summary>A name for loose matching: lower case, '-' and ' ' as '_', no leading "7_" or trailing "_0.1_16_16" numbers.</summary>
    /// <summary>Image files the importer reads (System.Drawing: no TGA or DDS).</summary>
    static readonly HashSet<string> ImageExt = [".png", ".jpg", ".jpeg", ".bmp"];

    /// <summary>A file by name without extension: .png first, then .jpg / .jpeg / .bmp.</summary>
    string? GetStem(string stem) => ImageExt.Select(e => Get(stem + e)).FirstOrDefault(x => x != null);

    /// <summary>The folder's own image whose name, without one of <paramref name="suffix"/>'s endings, the material's name holds
    /// (number prefixes / suffixes and case ignored): Captain Carter's "7_Char-CaptainCarter_0.1_16_16" finds Char_CaptainCarter_D.</summary>
    string? Loose(string material, Regex suffix, out bool exact)
    {
        exact = false;
        string want = Simple(material);
        var hits = local.Keys.Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(k => suffix.IsMatch(k))
            .Select(k => (File: k, Stem: Simple(suffix.Replace(k, "")))).Where(x => x.Stem.Length >= 4 && want.Contains(x.Stem))
            .OrderByDescending(x => x.Stem == want).ThenByDescending(x => x.Stem.Length).ToList();
        if (hits.Count == 0) return null;
        exact = hits[0].Stem == want;
        return GetStem(hits[0].File);
    }

    static readonly Regex ColourSuffix = new(@"_(d|diff|diffuse|albedo|basecolor|base_color|col|color|colour)$");
    static readonly Regex NormalSuffix = new(@"_(n|nor|nrm|norm|normal|normals|normalmap)$");

    static string Simple(string name)
    {
        string s = Regex.Replace(name.ToLowerInvariant(), @"[- ]", "_");
        s = Regex.Replace(s, @"^(\d+_)+", "");
        s = Regex.Replace(s, @"(_[\d.]+)+$", "");
        return s;
    }

    public Textures Find(string material, Material? mat)
    {
        var t = new Textures { Diffuse = Get(material + ".png"), Spec = Get(material + "_sp.png"), Alpha = Get(material + "_alpha.png") };
        if (t.Diffuse == null && mat is { HasTextureDiffuse: true })
            t.Diffuse = Get(Path.GetFileName(mat.TextureDiffuse.FilePath ?? "")) ?? GetStem(Path.GetFileNameWithoutExtension(mat.TextureDiffuse.FilePath ?? ""));
        if (t.Diffuse == null && material.Length > 0 && NearNames.TryGetValue(Near(material), out var nearName))
        {
            t.Guessed = true;
            t.Diffuse = Get(nearName + ".png"); t.Spec ??= Get(nearName + "_sp.png"); t.Alpha ??= Get(nearName + "_alpha.png");
        }
        // Rips from other games (Kurt's Captain Carter, Marvel Strike Force: material "7_Char-CaptainCarter_0.1_16_16", colour
        // map "Char_CaptainCarter_D.png" in a subfolder): the material's name without its number prefix / suffix against the
        // folder's own colour maps (name without _D / _Diffuse / _Albedo / _BaseColor / _Col); the same name, else the longest one it holds.
        if (t.Diffuse == null && material.Length > 0 && Loose(material, ColourSuffix, out bool exactColour) is string lc) { t.Diffuse = lc; t.Guessed = !exactColour; }
        // the model's own normal map: <material>_n / _normal / _nrm, the FBX material's normal slot, or a loose match as above
        t.Normal = GetStem(material + "_n") ?? GetStem(material + "_normal") ?? GetStem(material + "_nrm");
        if (t.Normal == null && mat is { HasTextureNormal: true } && Path.GetFileNameWithoutExtension(mat.TextureNormal.FilePath ?? "") is { Length: > 0 } ns) t.Normal = GetStem(ns);
        if (t.Normal == null && material.Length > 0) t.Normal = Loose(material, NormalSuffix, out _);
        t.SpecMho = SpecChannels.Find(sfx => GetStem(material + "_mhospec" + sfx));
        t.SpecColor = GetStem(material + "_speccolor");
        t.Glow = GetStem(material + "_glow") ?? GetStem(material + "_emissive") ?? GetStem(material + "_emit");
        if (t.Glow == null && mat is { HasTextureEmissive: true } && Path.GetFileNameWithoutExtension(mat.TextureEmissive.FilePath ?? "") is { Length: > 0 } es) t.Glow = GetStem(es);
        // Other maps of this material: <material>_<word>.png (e.g. _mask, _fx), not other models' files that share the prefix.
        var extra = new Regex("^" + Regex.Escape(material.ToLowerInvariant()) + "_(?!sp\\.|alpha\\.|glow\\.|emissive\\.|emit\\.)[a-z]+\\.png$");
        foreach (var k in local.Keys.Concat(Shared.Keys))
            if (extra.IsMatch(k) && Get(k) is string p && !t.Extra.Contains(p)) t.Extra.Add(p);
        return t;
    }
}
