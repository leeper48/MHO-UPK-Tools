using System.Numerics;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;
using AnimPackage = AnimExportCli.Packages.Package;

namespace MhoExtendedModManager.Model;

/// <summary>
/// An MHO skeletal mesh (the base hero the MFF model goes onto), read with AnimExportCli's reader, in the Mod Manager's
/// proven conventions: bone local = rotation × translation, model = local × parent (row vectors), the mesh's positions are
/// in the same model space (bind pose). Read only: packages are opened, never written.
/// </summary>
sealed class MhoSkeleton
{
    public required string Package;
    public required SkeletalMesh Mesh;
    public required Matrix4x4[] BoneToModel;
    public string Name => Mesh.Name;
    public IReadOnlyList<MeshBone> Bones => Mesh.Bones;
    public Vector3 Up, Left, Forward;   // measured, model space
    public string Frame = "";
    public float Height;                // feet to top of head, model units
    /// <summary>Share of LOD0 triangles whose corner order (a, b, c → (b−a)×(c−a)) points the same way as the stored
    /// vertex normals: tells which winding the engine treats as front.</summary>
    public float WindingAgree;
    public List<string> Warnings { get; } = new();

    HashSet<string>? simulated;
    /// <summary>Bones the game moves by physics (0.10.21): the base mesh's physics asset (&lt;mesh&gt;_physics) bodies that aren't
    /// bFixed (Black Cat: g_bangs, her hair and fur bones). Read only; empty when the asset can't be read.</summary>
    public IReadOnlySet<string> Simulated
    {
        get
        {
            if (simulated != null) return simulated;
            simulated = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var pkg = MhoPackageModifier.Package.Open(Package);
                string prefix = Name + "." + Name + "_physics.";
                for (int i = 0; i < pkg.Exports.Length; i++)
                {
                    if (!pkg.ClassOf(pkg.Exports[i]).Equals("RB_BodySetup", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!pkg.PathOf(pkg.Exports[i]).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] d = pkg.ReadExportBytes(pkg.Exports[i]).ToArray();
                    var tags = MhoPackageModifier.TagWalker.Walk(pkg, d, 4) ?? MhoPackageModifier.TagWalker.Walk(pkg, d, 16);
                    if (tags == null) continue;
                    string? bone = null; bool isFixed = false;
                    foreach (var t in tags)
                    {
                        if (t.Name.Equals("BoneName", StringComparison.OrdinalIgnoreCase)) bone = MhoPackageModifier.TagWalker.NameAt(pkg, d, t.ValueAt);
                        else if (t.Name.Equals("bFixed", StringComparison.OrdinalIgnoreCase)) isFixed = d[t.ValueAt - 1] != 0;
                    }
                    if (bone != null && !isFixed) simulated.Add(bone);
                }
            }
            catch (Exception) { }
            return simulated;
        }
    }

    public int Find(string name) { for (int i = 0; i < Bones.Count; i++) if (Bones[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
    public Vector3 Pos(int i) => BoneToModel[i].Translation;

    /// <summary>
    /// This skeleton with a donor hero's cape or hair bones added at the end (2026-10-02, the borrowed cape / hair prototype):
    /// every donor bone whose name matches <paramref name="part"/>, in groups under the bones they hang from (a cape: g_cape1
    /// on g_spine03; hair: g_hair1 and the front strands on g_head). Each group's root goes under the same-named bone here,
    /// at the donor's offset from it scaled by the heights, turned as the donor's in model space; the rest keep the donor's
    /// local pose (lengths scaled). Existing bones keep their indices. <paramref name="corrections"/>: per root, the constant
    /// turn carrying the donor's root keys into this skeleton's parent frame (root local × it). Null when the donor has no
    /// such bones, this skeleton has some already, or a parent bone is missing here.
    /// </summary>
    /// <summary>Bones grafted on from another hero (WithRig): chain matching lets several MFF strands share these (many-to-one).</summary>
    public IReadOnlySet<string> Borrowed { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public MhoSkeleton? WithRig(MhoSkeleton donor, System.Text.RegularExpressions.Regex part, out Dictionary<string, Matrix4x4> corrections, float stretch = 1)
    {
        corrections = new(StringComparer.OrdinalIgnoreCase);
        if (Bones.Any(b => part.IsMatch(b.Name))) return null;
        var rig = Enumerable.Range(0, donor.Bones.Count).Where(i => part.IsMatch(donor.Bones[i].Name)).ToList();
        if (rig.Count == 0) return null;
        bool InRig(int i) => i >= 0 && part.IsMatch(donor.Bones[i].Name);
        float scale = donor.Height > 0 && Height > 0 ? Height / donor.Height : 1;
        var list = Bones.ToList();
        var newIndex = new Dictionary<int, int>();
        static Matrix4x4 Rot(Matrix4x4 m) { m.Translation = Vector3.Zero; return m; }
        foreach (int b in rig)   // parents first (the skeleton is in tree order)
        {
            var db = donor.Bones[b];
            int dParent = db.ParentIndex == b ? -1 : db.ParentIndex;
            if (InRig(dParent)) { newIndex[b] = list.Count; list.Add(new MeshBone { Name = db.Name, ParentIndex = newIndex[dParent], Orientation = db.Orientation, Position = db.Position * scale * stretch }); continue; }
            if (dParent < 0) return null;
            int tParent = Find(donor.Bones[dParent].Name);
            if (tParent < 0) return null;
            var dRootG = donor.BoneToModel[b]; var dParentG = donor.BoneToModel[dParent]; var tParentG = BoneToModel[tParent];
            var rootG = Rot(dRootG);
            rootG.Translation = tParentG.Translation + (dRootG.Translation - dParentG.Translation) * scale;
            Matrix4x4.Invert(tParentG, out var invTP);
            var local = rootG * invTP;
            newIndex[b] = list.Count;
            list.Add(new MeshBone { Name = db.Name, ParentIndex = tParent, Orientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Rot(local))), Position = local.Translation });
            Matrix4x4.Invert(Rot(tParentG), out var invA);
            corrections[db.Name] = Rot(dParentG) * invA;
        }
        var mesh = Mesh with { Bones = list };
        var rest = SkeletonPose.Rest(mesh.Bones);
        var sk = new MhoSkeleton { Package = Package, Mesh = mesh, BoneToModel = rest.BoneToModel.ToArray() };
        sk.Measure();
        sk.Borrowed = new HashSet<string>(Borrowed.Concat(rig.Select(b => donor.Bones[b].Name)), StringComparer.OrdinalIgnoreCase);
        return sk;
    }

    /// <summary>Every skeletal mesh in a package (name, export index).</summary>
    public static List<(string Name, int Export)> List(string package)
    {
        var pkg = AnimPackage.Open(package);
        return pkg.FindExportsOfClass(SkeletalMeshReader.ClassName).Select(i => (pkg.GetExportName(i), i)).ToList();
    }

    /// <summary>Reads the named skeletal mesh, or the package's one with the most bones.</summary>
    public static MhoSkeleton Load(string package, string? meshName = null)
    {
        var pkg = AnimPackage.Open(package);
        var found = pkg.FindExportsOfClass(SkeletalMeshReader.ClassName).ToList();
        if (found.Count == 0) throw new InvalidDataException($"{Path.GetFileName(package)} has no skeletal mesh.");
        SkeletalMesh? mesh = null;
        string? failure = null;
        // Without a name: the character's body, not a prop rigged to the full skeleton (0.10.9; base packages hold props and
        // other characters' meshes: Punisher's only weapons and a flamethrower backpack, Cyclops's an Angel mesh bigger than
        // his own, Jean Grey's Phoenix wings, Rogue's wings). Prop-named meshes are skipped, a name with the hero's in it wins,
        // then the most bones.
        string hero = HeroOf(package);
        (int, int, int) Score(string name, SkeletalMesh m) => (PropName.IsMatch(name) ? 0 : 1,
            hero.Length > 0 && name.Replace("_", "").Contains(hero, StringComparison.OrdinalIgnoreCase) ? 1 : 0, m.Bones.Count);
        var best = (-1, -1, -1);
        var skipped = new List<string>();
        foreach (int i in found)
        {
            string name = pkg.GetExportName(i);
            if (meshName != null && !name.Equals(meshName, StringComparison.OrdinalIgnoreCase)) continue;
            var m = SkeletalMeshReader.TryRead(pkg, i, e => failure = e);
            if (m == null) continue;
            if (meshName == null && PropName.IsMatch(name)) { skipped.Add(name); continue; }
            var sc = Score(name, m);
            if (mesh == null || sc.CompareTo(best) > 0) { mesh = m; best = sc; }
        }
        if (mesh == null && skipped.Count > 0)
            throw new InvalidDataException($"{Path.GetFileName(package)} holds only props ({string.Join(", ", skipped.Take(4))}{(skipped.Count > 4 ? " …" : "")}): pick one of the hero's costume packages.");
        if (mesh == null) throw new InvalidDataException($"No readable skeletal mesh{(meshName != null ? " named " + meshName : "")} in {Path.GetFileName(package)}{(failure != null ? ": " + failure : "")}.");
        var rest = SkeletonPose.Rest(mesh.Bones);
        var sk = new MhoSkeleton { Package = package, Mesh = mesh, BoneToModel = rest.BoneToModel.ToArray() };
        sk.Measure();
        return sk;
    }

    static readonly System.Text.RegularExpressions.Regex PropName = new(
        @"(backpack|wing|gun|rifle|pistol|launcher|shotgun|m16|m60|bike|claw|blade|sword|shield|hammer|weapon|_prop|bow$|quiver|staff|spear|axe|knife)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The hero part of UC__MarvelPlayer_&lt;Hero&gt;[_Costume]_SF (lower case), or "".</summary>
    static string HeroOf(string package)
    {
        var mt = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(package), @"^UC__Marvel(?:Player|TeamUp)_([A-Za-z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return mt.Success ? mt.Groups[1].Value.ToLowerInvariant() : "";
    }

    void Measure()
    {
        int head = Find("g_head"), pelvis = Find("g_pelvis"), lt = Find("g_l_hip"), rt = Find("g_r_hip");
        if (head < 0 || pelvis < 0 || lt < 0 || rt < 0)
        {
            Warnings.Add("no g_head / g_pelvis / g_l_hip / g_r_hip: not a standard humanoid skeleton");
            return;
        }
        Up = Vector3.Normalize(Pos(head) - Pos(pelvis));
        var l = Pos(lt) - Pos(rt);
        Left = Vector3.Normalize(l - Vector3.Dot(l, Up) * Up);
        // MHO model space is Unreal's left-handed one (X forward, Y right, Z up): forward = up × left (checked: toes and eyes
        // at +X on Punisher, Storm, Hulk, Daredevil). MFF's normalized frame is right-handed, so MFF → MHO negates Y, which
        // also turns MFF's counter-clockwise triangles (99.9 %) into MHO's clockwise ones (0 % counter-clockwise).
        Forward = Vector3.Normalize(Vector3.Cross(Up, Left));
        Frame = $"up {Axis(Up)}, left {Axis(Left)}, forward {Axis(Forward)}";
        var lod = Mesh.HighestDetail;
        if (lod != null && lod.Positions.Count > 0)
            Height = lod.Positions.Max(p => Vector3.Dot(p, Up)) - lod.Positions.Min(p => Vector3.Dot(p, Up));
        if (lod != null && lod.Normals.Count == lod.Positions.Count)
        {
            int agree = 0, n = 0;
            for (int t = 0; t + 2 < lod.Indices.Count; t += 3)
            {
                var a = lod.Positions[lod.Indices[t]]; var b = lod.Positions[lod.Indices[t + 1]]; var c = lod.Positions[lod.Indices[t + 2]];
                var g = Vector3.Cross(b - a, c - a);
                if (g.LengthSquared() < 1e-12f) continue;
                var nn = lod.Normals[lod.Indices[t]] + lod.Normals[lod.Indices[t + 1]] + lod.Normals[lod.Indices[t + 2]];
                n++; if (Vector3.Dot(g, nn) > 0) agree++;
            }
            WindingAgree = n > 0 ? (float)agree / n : 0;
        }
        int foot = Find("g_l_ankle"), toe = Find("g_l_ball");
        if (foot >= 0 && toe >= 0 && Vector3.Dot(Pos(toe) - Pos(foot), Forward) <= 0) Warnings.Add("toes are not ahead of the foot: facing unclear");
    }

    static string Axis(Vector3 v)
    {
        var a = new[] { ("+X", v.X), ("-X", -v.X), ("+Y", v.Y), ("-Y", -v.Y), ("+Z", v.Z), ("-Z", -v.Z) }.MaxBy(t => t.Item2);
        return a.Item2 > 0.99f ? a.Item1 : $"{a.Item1}~({v.X:0.00} {v.Y:0.00} {v.Z:0.00})";
    }
}
