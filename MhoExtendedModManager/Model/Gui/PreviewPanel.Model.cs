using System.Diagnostics;
using System.Drawing.Imaging;
using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using MhoExtendedModManager;
using MhoExtendedModManager.Gui;
using MpmPackage = MhoPackageModifier.Package;

namespace MhoMffImporter.Gui;

/// <summary>The preview's model: the retarget prepared off the UI thread (looks, normal maps, animations, the base hero's own mesh for Compare).</summary>
sealed partial class PreviewPanel
{
    // --- the model --------------------------------------------------------------------------------------------------------------
    /// <summary>What the preview needs from a retarget, made off the UI thread (looks decoded, normal maps generated).</summary>
    public sealed record Prepared(ModMeshes.Loaded Mesh, List<MeshBone> Bones, List<AnimRef> Animations, string Note, ModMeshes.Loaded? Stock = null, string Material = "")
    {
        /// <summary>No source picked: the target's own model only (shown as Compare, which stays on).</summary>
        public bool TargetOnly { get; init; }
        /// <summary>The bone map the retarget used (MFF sources), for the Bone Map tab.</summary>
        public BoneMapFile? Map { get; init; }
        /// <summary>Each material and its maps as used (after the Materials tab's overrides), for the Materials tab.</summary>
        public List<(string Material, Textures Tex)> MaterialList { get; init; } = [];
        /// <summary>The MHO skeleton's bone names (the Bone Map tab's choices) and each one's parent (-1 = root).</summary>
        public List<string> MhoBones { get; init; } = [];
        public List<int> MhoParents { get; init; } = [];
        /// <summary>The base package (full path), its skeletal meshes and the base hero's own one among them (props and power
        /// effects are read from there, 0.13.0).</summary>
        public string PackagePath { get; init; } = "";
        public List<MeshRef> PackageMeshes { get; init; } = [];
        public MeshRef? MainRef { get; init; }
        /// <summary>A cape and / or long hair borrowed from other heroes (preview prototype): put into each animation as it loads.</summary>
        public List<BorrowedRig> Rigs { get; init; } = [];
    }

    /// <summary>Retargets <paramref name="model"/>'s parts onto the base package's skeleton and prepares the preview (worker thread).</summary>
    public static Prepared Prepare(MffModel model, string? parts, string packagePath, string? materialDonor, bool subdivide = false, string? modelFbx = null, string? mapFile = null,
        int cape = 0, int hair = 0, string? overrides = null)
    {
        var sk = MhoSkeleton.Load(packagePath, null);
        var rigs = new List<BorrowedRig>(); var notes = new List<string>();
        foreach (var (kind, n) in new[] { (BorrowedRig.Kind.Cape, cape), (BorrowedRig.Kind.Hair, hair) })
        {
            if (n <= 0) continue;
            (sk, var rig) = BorrowedRig.Graft(sk, kind, n, out string note, kind == BorrowedRig.Kind.Hair ? BorrowedRig.HairDrop(model) : 0);
            notes.Add(note);
            if (rig != null) rigs.Add(rig);
        }
        var picked = model.Selected(parts);
        if (subdivide) picked = Subdivision.Apply(picked);
        var file = mapFile != null ? BoneMapFile.Load(mapFile) : null;
        var r = Retarget.Run(model, picked, sk, file);
        if (modelFbx != null) FbxReimport.Apply(r, modelFbx, _ => { });
        MaterialOverrides.Apply(r, overrides);
        var p = FromRetarget(r, sk, packagePath, materialDonor);
        if (p.Map != null && file != null) p.Map.Smooth = file.Smooth;   // the map shown keeps the file's smoothing
        if (notes.Count > 0)
        {
            // how many of the model's chains ride the borrowed bones (none: its strips are too far from them to pair)
            foreach (var rig in rigs) { rig.Paired = r.ChainPairs.Count(c => BorrowedRig.Pattern(rig.Part).IsMatch(c.MhoRoot)); rig.MeasureBody(p.Bones, r); }
            p = p with { Rigs = rigs, Note = p.Note + " · " + string.Join(" · ", notes) + " · " + string.Join(", ", rigs.Select(x => $"{x.Paired} model chain(s) ride {x.Label}")) };
        }
        return p;
    }

    /// <summary>An FBX as the source (0.11.3): its skeleton proportions, meshes (<paramref name="meshes"/>, null = all) and textures.</summary>
    public static Prepared PrepareFbx(string fbx, IReadOnlyCollection<string>? meshes, string packagePath, string? materialDonor, string? mapFile = null, string? overrides = null, string? modelFbx = null)
    {
        var sk = MhoSkeleton.Load(packagePath, null);
        var r = FbxReimport.Load(fbx, sk, meshes, _ => { });
        if (modelFbx != null) FbxReimport.Apply(r, modelFbx, _ => { });   // the mesh edited in Blender (Ctrl+S)
        MaterialOverrides.Apply(r, overrides);
        // the Bone Map's smoothing (an FBX source's map holds only that)
        var file = mapFile != null ? BoneMapFile.Load(mapFile) : null;
        if (file is { Smooth.Count: > 0 }) WeightSmooth.Apply(r, file.Smooth);
        var p = FromRetarget(r, sk, packagePath, materialDonor);
        if (p.Map != null && file != null) p.Map.Smooth = file.Smooth;
        return p;
    }

    /// <summary>
    /// The Bone Map of an FBX source (Kurt, 2026-10-04: the list was empty): its bones are the hero's by name, so nothing is
    /// paired; each row says whether the FBX has the bone and how many vertices it moves (none: that part doesn't bend with
    /// it). Read only: an FBX's bones and weights are changed in Blender.
    /// </summary>
    static BoneMapFile FbxBoneList(Retargeted r, string label)
    {
        var count = new int[r.Bones.Count];
        foreach (var s in r.Sections) foreach (var w in s.Weights) foreach (var (b, wt) in w) if (wt > 0 && b >= 0 && b < count.Length) count[b]++;
        var f = new BoneMapFile { Mff = "FBX", Mho = label };
        for (int i = 0; i < r.Bones.Count; i++)
        {
            var b = r.Bones[i];
            if (!b.Mapped && count[i] == 0) continue;   // neither in the FBX nor weighted: the hero's own extra bones
            f.Bones.Add(new BoneMapFile.BoneEntry
            {
                Mff = b.Name, Mho = b.Name,
                How = (b.Mapped ? "from the FBX" : "not in the FBX (at the hero's place)") + (count[i] > 0 ? $" · {count[i]:N0} vertices" : " · no weights"),
            });
        }
        return f;
    }

    /// <summary>The target's own model with its animations, for when no source is picked (Kurt, 2026-10-04: the preview
    /// shows the package on the right on its own, as Compare).</summary>
    public static Prepared PrepareTarget(string packagePath)
    {
        var sk = MhoSkeleton.Load(packagePath, null);
        string file = Path.GetFileName(packagePath);
        string? cooked = Settings.Current.CookedFolder ?? Settings.Current.StockFolder;
        var pkgMeshes = ModMeshes.List([(file, packagePath)], anyPackage: true);
        var mr = pkgMeshes.FirstOrDefault(x => x.Name.Equals(sk.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{file} has no mesh named {sk.Name}");
        var stock = ModMeshes.Load(mr, cooked, out _) ?? throw new InvalidDataException($"{sk.Name} couldn't be read");
        var bones = stock.Bones.ToList();
        var list = ModAnimations.For(new MeshRef(file, packagePath, sk.Name, 0), bones, [], cooked);
        return new Prepared(stock, bones, list, $"{sk.Name}: the target's own model (pick a source on the left to put a model on it) · {list.Count} animations", stock, "")
        {
            TargetOnly = true, MhoBones = sk.Bones.Select(b => b.Name).ToList(),
            MhoParents = sk.Bones.Select((b, i) => b.ParentIndex == i ? -1 : b.ParentIndex).ToList(),
            PackagePath = packagePath, PackageMeshes = pkgMeshes, MainRef = mr,
        };
    }

    static Prepared FromRetarget(Retargeted r, MhoSkeleton sk, string packagePath, string? materialDonor)
    {
        var bones = MhoAnim.FromGlobals(r.Bones.Select(b => (b.Name, b.Parent, b.Global)).ToList());
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>(); var triSec = new List<int>();
        var infl = new List<VertexInfluence>();
        var looks = new List<ModelView.Look?>();
        // the material the build would choose
        float metal = r.Sections.Any(x => x.Tex.UsesMhoSpec) ? 1   // an MHO spec map: the Metal template, as the build
            : MaterialChoice.MetalShare(r.Sections.Select(x => x.Tex.Spec).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        float glowShare = metal > 0.5f && !r.Sections.Any(x => x.Tex.UsesMhoSpec) ? MaterialChoice.GlowShare(r.Sections.Select(x => x.Tex.Diffuse).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) : 0;
        string? donor = MaterialChoice.Donor(materialDonor, metal, glowShare);
        bool metalStyle = donor is MaterialChoice.Metal or MaterialChoice.Glow, glowOn = donor == MaterialChoice.Glow;
        var lookCache = new Dictionary<string, ModelView.Look>(StringComparer.OrdinalIgnoreCase);
        for (int si = 0; si < r.Sections.Count; si++)
        {
            var s = r.Sections[si];
            int start = pos.Count;
            pos.AddRange(s.Pos); nrm.AddRange(s.Normal);
            uv.AddRange(s.Uv.Select(u => new Vector2(u.X, 1 - u.Y)));   // FBX bottom-left → texture top-left (as the build)
            foreach (var w in s.Weights) infl.Add(new VertexInfluence { Bones = w.Select(x => x.Bone).ToArray(), Weights = w.Select(x => x.Weight).ToArray() });
            foreach (int t in s.Tris) idx.Add(start + t);
            for (int t = 0; t < s.Tris.Length / 3; t++) triSec.Add(si);
            string key = s.Material;
            if (!lookCache.TryGetValue(key, out var look)) lookCache[key] = look = LookFor(s.Tex, metalStyle, glowOn);
            looks.Add(look);
        }
        var p = pos.ToArray(); var n = nrm.ToArray(); var u2 = uv.ToArray(); var i2 = idx.ToArray();
        var tan = ModMeshes.Tangents(p, n, u2, i2);
        var mesh = new ModMeshes.Loaded(sk.Name, p, n, tan, u2, i2, triSec.ToArray(), looks.ToArray(), "", bones, infl);
        string file = Path.GetFileName(packagePath);
        var list = ModAnimations.For(new MeshRef(file, packagePath, sk.Name, 0), bones, [], Settings.Current.CookedFolder ?? Settings.Current.StockFolder);
        string material = donor == null ? "the base mesh's own" : donor == MaterialChoice.Glow ? "metal + glow" : donor == MaterialChoice.Metal ? "metal" : donor == MaterialChoice.Default ? "cloth" : donor.Split(':').Last();
        // the base hero's own mesh, for Compare
        ModMeshes.Loaded? stock = null;
        List<MeshRef> pkgMeshes = [];
        MeshRef? mr = null;
        try
        {
            pkgMeshes = ModMeshes.List([(file, packagePath)], anyPackage: true);
            mr = pkgMeshes.FirstOrDefault(x => x.Name.Equals(sk.Name, StringComparison.OrdinalIgnoreCase));
            if (mr != null) stock = ModMeshes.Load(mr, Settings.Current.CookedFolder ?? Settings.Current.StockFolder, out _);
        }
        catch (Exception) { }
        return new Prepared(mesh, bones, list, $"{p.Length:N0} vertices on {sk.Name} · material: {material} · {list.Count} animations", stock,
            donor == null ? "Base Mesh's Own" : donor == MaterialChoice.Glow ? "Metal + Glow" : donor == MaterialChoice.Metal ? "Metal" : donor == MaterialChoice.Default ? "Cloth" : donor.Split(':').Last())
        {
            Map = r.Source != null ? BoneMapFile.From(r, sk.Name) : FbxBoneList(r, sk.Name), MhoBones = sk.Bones.Select(b => b.Name).ToList(),
            MhoParents = sk.Bones.Select((b, i) => b.ParentIndex == i ? -1 : b.ParentIndex).ToList(),
            PackagePath = packagePath, PackageMeshes = pkgMeshes, MainRef = mr,
            MaterialList = r.Sections.GroupBy(x => x.Material).Select(g => (g.Key, g.First().Tex)).ToList(),
        };
    }

    /// <summary>A section's look from its MFF maps, as the build packs them.</summary>
    static ModelView.Look LookFor(Textures tex, bool metalStyle, bool glowOn)
    {
        var colour = tex.Diffuse != null ? LoadBgra(tex.Diffuse) : null;
        if (colour != null && tex.Alpha != null && LoadBgra(tex.Alpha) is { } a && a.W == colour.Value.W && a.H == colour.Value.H)
            for (int i = 0; i < a.Px.Length; i += 4) colour.Value.Px[i + 3] = (byte)((a.Px[i] + a.Px[i + 1] + a.Px[i + 2]) / 3);
        var look = ModelView.Look.Plain(colour is { } c ? new ModelView.Map(c.Px, c.W, c.H) : null);
        look.Cutout = tex.Alpha != null;
        look.UseRim = true; look.HalfLambert = true; look.Fill = true;
        if (tex.Normal != null && LoadBgra(tex.Normal) is { } own)
        {
            // the model's own normal map (Kurt, 2026-10-04), green flipped when it's OpenGL style
            if (tex.NormalFlipGreen) for (int i = 1; i < own.Px.Length; i += 4) own.Px[i] = (byte)(255 - own.Px[i]);
            look.Normal = new ModelView.Map(own.Px, own.W, own.H); look.UseNormal = true; look.NormalStrength = 1;
        }
        else if (colour is { } cc)
        {
            // the normal map the build generates from the colour map (DirectX green, as MHO's)
            int[] argb = new int[cc.W * cc.H];
            for (int i = 0; i < argb.Length; i++) argb[i] = cc.Px[4 * i] | cc.Px[4 * i + 1] << 8 | cc.Px[4 * i + 2] << 16 | cc.Px[4 * i + 3] << 24;
            var nm = NormalMapGen.Make(cc.W, cc.H, argb, new NormalMapSettings(), null);
            var nb = new byte[nm.Length * 4];
            for (int i = 0; i < nm.Length; i++) { nb[4 * i] = (byte)nm[i]; nb[4 * i + 1] = (byte)(nm[i] >> 8); nb[4 * i + 2] = (byte)(nm[i] >> 16); nb[4 * i + 3] = 255; }
            look.Normal = new ModelView.Map(nb, cc.W, cc.H); look.UseNormal = true; look.NormalStrength = 1;
        }
        bool mhoSpec = false;
        var mhoMap = tex.SpecMho != null ? LoadBgra(tex.SpecMhoAngela!) : null;
        if (mhoMap == null && tex.ColorTags is { Count: > 0 } ctags && colour is { } ccol)
            mhoMap = (ColorTags.MakePacked(ccol.W, ccol.H, ccol.Px, ctags), ccol.W, ccol.H);   // from the user's color tags, as the build
        if (mhoMap is { } mho)
        {
            mhoSpec = true;
            // MHO's own packed map, read as Angela's armor material reads it: R shine, G spec power (× specularpower 5 × mask
            // 25), A reflectivity (reflectionmult 20, by the diffuse); the spec color map tints the highlight
            var map = new ModelView.Map(mho.Px, mho.W, mho.H);
            look.UseSpec = true; look.Spec = new ModelView.Channel(map, 0); look.SpecPow = new ModelView.Channel(map, 1); look.SkinMask = new ModelView.Channel(map, 2);
            look.SpecStrength = 1; look.SpecPower = 5; look.SpecPowerMask = 25;
            look.SpecColor = tex.SpecColor != null && LoadBgra(tex.SpecColor) is { } scm ? new ModelView.Map(scm.Px, scm.W, scm.H) : look.Diffuse;
            if (Reflection() is { } env) { look.UseReflection = true; look.Reflection = env; look.ReflectAt = new ModelView.Channel(map, 3); look.ReflectMult = 2; look.ReflectByDiffuse = true; look.FresnelPower = 1; }
        }
        var spOrGen = !mhoSpec && tex.Spec != null ? LoadBgra(tex.Spec) : null;
        if (spOrGen == null && !mhoSpec && colour is { } gcol)
        {
            // none of its own: generated from the color map as the build does (SpecMapGen; R = shine)
            var shine = SpecMapGen.Make(gcol.W, gcol.H, gcol.Px, tex.SpecRecipe);
            var gp = new byte[shine.Length * 4];
            for (int i = 0; i < shine.Length; i++) { gp[4 * i + 2] = shine[i]; gp[4 * i + 3] = 255; }
            spOrGen = (gp, gcol.W, gcol.H);
        }
        if (spOrGen is { } sp)
        {
            // _sp red = shine; blue = MFF's metal mask (MaterialMaps.Metal)
            var packed = new byte[sp.Px.Length];
            for (int i = 0; i < sp.Px.Length; i += 4)
            {
                var px = Color.FromArgb(sp.Px[i + 3], sp.Px[i + 2], sp.Px[i + 1], sp.Px[i]);
                float m = metalStyle ? MaterialMaps.Metal(px) : 0;
                packed[i + 2] = (byte)Math.Min(255, px.R * (metalStyle ? 3.5f : 1f));   // R spec
                packed[i + 1] = (byte)(metalStyle ? 45 + 70 * m : 15);                 // G spec power
                packed[i + 3] = (byte)(130 * m);                                       // A reflectivity
            }
            var map = new ModelView.Map(packed, sp.W, sp.H);
            look.UseSpec = true; look.Spec = new ModelView.Channel(map, 0); look.SpecStrength = 1; look.SpecPower = metalStyle ? 40 : 16;
            look.PowerShown = new ModelView.Channel(map, 1);   // (what the build packs as spec power: shown, not shaded by)
            look.SpecColor = look.Diffuse;
            if (metalStyle && Reflection() is { } env)
            {
                look.UseReflection = true; look.Reflection = env; look.ReflectAt = new ModelView.Channel(map, 3);
                look.ReflectMult = 2; look.ReflectByDiffuse = true; look.FresnelPower = 1;
            }
        }
        if (glowOn && colour is { } gc)
        {
            var g = new byte[gc.Px.Length];
            for (int i = 0; i < g.Length; i += 4)
            {
                var px = Color.FromArgb(255, gc.Px[i + 2], gc.Px[i + 1], gc.Px[i]);
                if (MaterialMaps.Glows(px)) { g[i] = gc.Px[i]; g[i + 1] = gc.Px[i + 1]; g[i + 2] = gc.Px[i + 2]; }
                g[i + 3] = 255;
            }
            look.EmissiveTex = new ModelView.Map(g, gc.W, gc.H); look.UseEmissive = true; look.EmissiveMult = 1;
        }
        return look;
    }

    /// <summary>An image as BGRA bytes.</summary>
    static (byte[] Px, int W, int H)? LoadBgra(string png)
    {
        try
        {
            using var src = new Bitmap(png);
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            using var b = src.Clone(rect, PixelFormat.Format32bppArgb);
            var d = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[d.Stride * d.Height];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
            b.UnlockBits(d);
            return (px, src.Width, src.Height);
        }
        catch (Exception) { return null; }
    }

    static ModelView.Map? reflection; static bool reflectionTried;
    /// <summary>Angela's reflection image (bw_reflect, from her stock package): what the metal materials reflect.</summary>
    static ModelView.Map? Reflection()
    {
        if (reflectionTried) return reflection;
        reflectionTried = true;
        try
        {
            var pkg = MpmPackage.Open(BasePackage.Resolve("UC__MarvelPlayer_Angela_SF.upk", true));
            int i = Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals("bw_reflect", StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            var mip = MhoPackageModifier.TextureExport.ReadBestMip(pkg, i, out _, Settings.Current.CookedFolder);
            var bgra = mip == null ? null : MhoPackageModifier.TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _);
            if (bgra != null) reflection = new ModelView.Map(bgra, mip!.Width, mip.Height);
        }
        catch (Exception) { }
        return reflection;
    }
}
