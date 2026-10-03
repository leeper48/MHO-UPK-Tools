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
        /// <summary>The bone map the retarget used (MFF sources), for the Bone Map tab.</summary>
        public BoneMapFile? Map { get; init; }
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
        int cape = 0, int hair = 0)
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
    public static Prepared PrepareFbx(string fbx, IReadOnlyCollection<string>? meshes, string packagePath, string? materialDonor)
    {
        var sk = MhoSkeleton.Load(packagePath, null);
        var r = FbxReimport.Load(fbx, sk, meshes, _ => { });
        return FromRetarget(r, sk, packagePath, materialDonor);
    }

    static Prepared FromRetarget(Retargeted r, MhoSkeleton sk, string packagePath, string? materialDonor)
    {
        var bones = MhoAnim.FromGlobals(r.Bones.Select(b => (b.Name, b.Parent, b.Global)).ToList());
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>(); var triSec = new List<int>();
        var infl = new List<VertexInfluence>();
        var looks = new List<ModelView.Look?>();
        // the material the build would choose
        float metal = MaterialChoice.MetalShare(r.Sections.Select(x => x.Tex.Spec).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        float glowShare = metal > 0.5f ? MaterialChoice.GlowShare(r.Sections.Select(x => x.Tex.Diffuse).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) : 0;
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
            Map = r.Source != null ? BoneMapFile.From(r, sk.Name) : null, MhoBones = sk.Bones.Select(b => b.Name).ToList(),
            MhoParents = sk.Bones.Select((b, i) => b.ParentIndex == i ? -1 : b.ParentIndex).ToList(),
            PackagePath = packagePath, PackageMeshes = pkgMeshes, MainRef = mr,
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
        if (colour is { } cc)
        {
            // the normal map the build generates from the colour map (DirectX green, as MHO's)
            int[] argb = new int[cc.W * cc.H];
            for (int i = 0; i < argb.Length; i++) argb[i] = cc.Px[4 * i] | cc.Px[4 * i + 1] << 8 | cc.Px[4 * i + 2] << 16 | cc.Px[4 * i + 3] << 24;
            var nm = NormalMapGen.Make(cc.W, cc.H, argb, new NormalMapSettings(), null);
            var nb = new byte[nm.Length * 4];
            for (int i = 0; i < nm.Length; i++) { nb[4 * i] = (byte)nm[i]; nb[4 * i + 1] = (byte)(nm[i] >> 8); nb[4 * i + 2] = (byte)(nm[i] >> 16); nb[4 * i + 3] = 255; }
            look.Normal = new ModelView.Map(nb, cc.W, cc.H); look.UseNormal = true; look.NormalStrength = 1;
        }
        if (tex.Spec != null && LoadBgra(tex.Spec) is { } sp)
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
