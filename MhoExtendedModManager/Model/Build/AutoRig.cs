using System.Diagnostics;
using System.Numerics;
using Assimp;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace MhoMffImporter;

/// <summary>
/// An FBX with no armature (Kurt, 2026-10-03: unrigged meshes as a Model tab source). Two steps:
/// <list type="number">
/// <item><see cref="Fit"/>: the meshes stood up in the base hero's model space. Up is the file's tallest axis; front is the
/// narrower of the two others, its sign from the feet (the soles reach further forward than the shins); scaled to the hero's
/// height, feet on the hero's floor, centred under the hero's pelvis.</item>
/// <item><see cref="Rig"/>: that mesh with the hero's skeleton written as model.fbx (<see cref="FbxExport"/>), then Blender
/// (headless) binds it with Automatic Weights (heat weighting) to the bones the hero's own body is weighted to (not root, IK,
/// offsets, capes or props); vertices heat weighting can't reach (loose pieces) go to the nearest bone. Saved as rig.blend and
/// exported as rigged.fbx, an FBX with MHO bone names: the tab's FBX source route reads it from there.</item>
/// </list>
/// <see cref="Open"/> opens rig.blend with a save hook: every Ctrl+S exports rigged.fbx again, and the tab reloads it.
/// </summary>
static class AutoRig
{
    /// <summary>A rig's name: &lt;fbx name&gt; on &lt;package&gt;.</summary>
    public static string Name(string fbx, string package) =>
        $"{FbxExport.SafeName(Path.GetFileNameWithoutExtension(fbx))} on {Path.GetFileNameWithoutExtension(package)}";

    /// <summary>
    /// Where a rig is worked on (data\model\rigs\&lt;name&gt; &lt;id&gt;; the id is from the source's full path, so two files of the
    /// same name don't share one): outside the editor's work folder, so a Blender left open keeps sending after Save or Close
    /// (Kurt, 2026-10-04). The mod keeps a copy of it (<see cref="Kept"/>); the newer one wins (<see cref="Newer"/>).
    /// </summary>
    public static string Live(string fbx, string package)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(fbx).ToLowerInvariant()));
        return Path.Combine(Settings.Home, "rigs", $"{Name(fbx, package)} {Convert.ToHexString(hash)[..6].ToLowerInvariant()}");
    }

    /// <summary>The mod's copy of a rig (in its Model folder: rigs\&lt;name&gt;): only what a rig needs, for the base heroes built onto.</summary>
    public static string Kept(string workFolder, string fbx, string package) => Path.Combine(workFolder, "rigs", Name(fbx, package));

    /// <summary>What a rig is: the Blender scene, the rigged FBX the tab reads, and the textures beside them. The rest (Blender's
    /// .blend1 backups, the scripts, the guide's lists) is made again when needed.</summary>
    public static bool IsRigFile(string fileName) =>
        fileName.Equals("rig.blend", StringComparison.OrdinalIgnoreCase) || fileName.Equals("rigged.fbx", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies a rig's files (<see cref="IsRigFile"/>) from one folder to another.</summary>
    public static void CopyRig(string from, string to)
    {
        Protected.CheckWrite(to);
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from).Where(f => IsRigFile(Path.GetFileName(f))))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }

    /// <summary>Whether <paramref name="a"/>'s rig was saved after <paramref name="b"/>'s (or b has none).</summary>
    public static bool Newer(string a, string b) =>
        File.Exists(RiggedFbx(a)) && (!File.Exists(RiggedFbx(b)) || File.GetLastWriteTimeUtc(RiggedFbx(a)) > File.GetLastWriteTimeUtc(RiggedFbx(b)));

    public static string RiggedFbx(string folder) => Path.Combine(folder, "rigged.fbx");

    /// <summary>The hero's own body in model.fbx (a guide for the weights; Blender deletes it).</summary>
    const string GuideName = "MHO_STOCK_GUIDE";

    /// <summary>A finger joint (MHO names: g_l_index1 … g_r_pinky3).</summary>
    static readonly System.Text.RegularExpressions.Regex Finger = new(@"^g_[lr]_(thumb|index|birdy|ring|pinky)\d$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Bones a foreign model isn't weighted to: face details, the hero's own hair, cape and cloth chains, and palm bones
    /// (grip points: Captain America's runs through the fingers, which then took none of the finger bones).</summary>
    static readonly System.Text.RegularExpressions.Regex FaceOrChain = new(
        @"(eye|lid|brow|lip|jaw|cheek|mouth|tongue|teeth|nose|ear_|hair|ponytail|braid|cape|cloak|coat|skirt|cloth|scarf|tail|tentacle|wing|palm)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Whether the FBX's meshes are skinned to bones at all.</summary>
    public static bool HasArmature(string fbx) => SkeletonProfile.Open(fbx) is { } s && s.Meshes.Any(m => m.BoneCount > 0);

    /// <summary>The meshes in the hero's model space, each vertex on g_pelvis for now (step 1).</summary>
    public static Retargeted Fit(string fbx, MhoSkeleton sk, List<string> notes)
    {
        var scene = SkeletonProfile.Open(fbx, PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices)
            ?? throw new InvalidDataException("Assimp couldn't read " + fbx);
        if (scene.MeshCount == 0) throw new InvalidDataException("the FBX holds no mesh");
        var global = new Dictionary<Node, Matrix4x4>();
        void Walk(Node n, Matrix4x4 parent)
        {
            var g = MffModel.ToNumerics(n.Transform) * parent;
            global[n] = g;
            foreach (var c in n.Children) Walk(c, g);
        }
        Walk(scene.RootNode, Matrix4x4.Identity);
        var texIndex = new TextureIndex(Path.GetDirectoryName(Path.GetFullPath(fbx))!);
        var parts = new List<(string Material, Textures Tex, Vector3[] Pos, Vector3[] Nrm, Vector2[] Uv, int[] Tris)>();
        foreach (var (node, g) in global)
            foreach (int mi in node.MeshIndices)
            {
                var mesh = scene.Meshes[mi];
                int nv = mesh.VertexCount;
                var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv];
                for (int v = 0; v < nv; v++)
                {
                    var p = mesh.Vertices[v]; pos[v] = Vector3.Transform(new Vector3(p.X, p.Y, p.Z), g);
                    var n = mesh.HasNormals ? mesh.Normals[v] : new Vector3D(0, 0, 1);
                    var tn = Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), g);
                    nrm[v] = tn.LengthSquared() > 1e-12f ? Vector3.Normalize(tn) : Vector3.UnitZ;
                    if (mesh.HasTextureCoords(0)) uv[v] = new Vector2(mesh.TextureCoordinateChannels[0][v].X, mesh.TextureCoordinateChannels[0][v].Y);
                }
                var tris = mesh.Faces.Where(f => f.IndexCount == 3).SelectMany(f => f.Indices).ToArray();
                var mat = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < scene.MaterialCount ? scene.Materials[mesh.MaterialIndex] : null;
                string matName = mat?.Name is { Length: > 0 } mn ? mn : mesh.Name;
                parts.Add((matName, texIndex.Find(matName, mat), pos, nrm, uv, tris));
            }
        var all = parts.SelectMany(p => p.Pos).ToList();
        if (all.Count == 0) throw new InvalidDataException("the FBX's meshes have no vertices");

        // up: the tallest file axis; front: the narrower of the other two, pointing where the feet do
        var lo = all.Aggregate(Vector3.Min); var hi = all.Aggregate(Vector3.Max); var ext = hi - lo;
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        float E(Vector3 a) => Vector3.Dot(ext, a);
        var byExt = axes.OrderByDescending(E).ToList();
        var up = byExt[0];
        if (E(byExt[1]) > 0.8f * E(byExt[0]))
        {
            // nearly as wide as tall (a T pose: Kurt's Juggernaut, arm span 1.87 against 1.77 tall, came in lying on his side):
            // a body is mirror-symmetric left to right, not top to bottom, so the more symmetric of the two runs across it
            float Asym(Vector3 a)
            {
                float l = Vector3.Dot(lo, a), span = MathF.Max(1e-6f, Vector3.Dot(hi, a) - l);
                var hist = new int[32];
                foreach (var p in all) hist[Math.Clamp((int)((Vector3.Dot(p, a) - l) / span * 32), 0, 31)]++;
                float d = 0; for (int i = 0; i < 16; i++) d += Math.Abs(hist[i] - hist[31 - i]);
                return d / all.Count;
            }
            float a0 = Asym(byExt[0]), a1 = Asym(byExt[1]);
            up = a0 >= a1 ? byExt[0] : byExt[1];
            notes.Add($"up: the two longest sides are close ({E(byExt[0]):0.##} and {E(byExt[1]):0.##}, a T pose?): {Axis(up)} is up, the less mirror-symmetric one (asymmetry {MathF.Max(a0, a1):0.00} against {MathF.Min(a0, a1):0.00})");
        }
        // which end is the top: the one most of the mesh is nearer (the head, hands and torso hold most of the detail)
        {
            float mean = all.Average(p => Vector3.Dot(p, up)), mid = (Vector3.Dot(lo, up) + Vector3.Dot(hi, up)) / 2;
            if (mean < mid) { up = -up; notes.Add($"up: the model is upside down in the file (most of it is toward -{Axis(up)}): turned over"); }
        }
        var flat = axes.Where(a => a != up && a != -up).OrderBy(E).ToList();
        var front = flat[0];
        float floor = all.Min(p => Vector3.Dot(p, up)), height = all.Max(p => Vector3.Dot(p, up)) - floor;
        var soles = all.Where(p => Vector3.Dot(p, up) - floor < 0.04f * height).ToList();
        var shins = all.Where(p => Vector3.Dot(p, up) - floor is var h && h > 0.15f * height && h < 0.3f * height).ToList();
        float toes = soles.Count > 0 && shins.Count > 0 ? soles.Average(p => Vector3.Dot(p, front)) - shins.Average(p => Vector3.Dot(p, front)) : 0;
        if (MathF.Abs(toes) > 0.005f * height) { if (toes < 0) front = -front; notes.Add($"front: the feet point along {(toes < 0 ? "-" : "+")}{Axis(front)} of the file"); }
        else
        {
            // no telling from the feet: Y-up files usually face +Z (Mixamo, Unity), Z-up ones -Y (Blender's front view)
            front = up == Vector3.UnitY ? Vector3.UnitZ : up == Vector3.UnitZ ? -Vector3.UnitY : front;
            notes.Add($"front: couldn't tell from the feet; taken as {Axis(front)} (the usual way for a {Axis(up)}-up file): if the model faces backwards, fix it in Blender");
        }
        var left = Vector3.Cross(up, front);

        // into the hero's space: scaled to its height, on its floor, under its pelvis
        var stock = sk.Mesh.HighestDetail?.Positions ?? [];
        float stockFloor = stock.Count > 0 ? stock.Min(p => Vector3.Dot(p, sk.Up)) : 0;
        float scale = sk.Height > 0 ? sk.Height / height : 1;
        int pelvis = Math.Max(0, sk.Find("g_pelvis"));
        var pel = sk.Pos(pelvis);
        float cf = Vector3.Dot((lo + hi) / 2, front), cl = Vector3.Dot((lo + hi) / 2, left);
        Vector3 Place(Vector3 p) =>
            ((Vector3.Dot(p, front) - cf) * scale + Vector3.Dot(pel, sk.Forward)) * sk.Forward
            + ((Vector3.Dot(p, left) - cl) * scale + Vector3.Dot(pel, sk.Left)) * sk.Left
            + ((Vector3.Dot(p, up) - floor) * scale + stockFloor) * sk.Up;
        Vector3 Turn(Vector3 n) => Vector3.Normalize(Vector3.Dot(n, front) * sk.Forward + Vector3.Dot(n, left) * sk.Left + Vector3.Dot(n, up) * sk.Up);
        // a turn keeps the file's counter-clockwise corners counter-clockwise; MHO's are clockwise: swap two corners then
        // (a mirror, as the MFF route's, flips them by itself)
        var m = new Matrix4x4(
            Vector3.Dot(Turn(Vector3.UnitX), Vector3.UnitX), Vector3.Dot(Turn(Vector3.UnitX), Vector3.UnitY), Vector3.Dot(Turn(Vector3.UnitX), Vector3.UnitZ), 0,
            Vector3.Dot(Turn(Vector3.UnitY), Vector3.UnitX), Vector3.Dot(Turn(Vector3.UnitY), Vector3.UnitY), Vector3.Dot(Turn(Vector3.UnitY), Vector3.UnitZ), 0,
            Vector3.Dot(Turn(Vector3.UnitZ), Vector3.UnitX), Vector3.Dot(Turn(Vector3.UnitZ), Vector3.UnitY), Vector3.Dot(Turn(Vector3.UnitZ), Vector3.UnitZ), 0,
            0, 0, 0, 1);
        bool swap = m.GetDeterminant() > 0;

        var r = new Retargeted { Source = null, Target = sk, Scale = scale };
        for (int i = 0; i < sk.Bones.Count; i++)
            r.Bones.Add(new RefBone { Name = sk.Bones[i].Name, Parent = sk.Bones[i].ParentIndex == i ? -1 : sk.Bones[i].ParentIndex, Global = sk.BoneToModel[i], Mapped = true });
        foreach (var (mat, tex, pos, nrm, uv, tris) in parts)
        {
            var t = (int[])tris.Clone();
            if (swap) for (int k = 0; k + 2 < t.Length; k += 3) (t[k + 1], t[k + 2]) = (t[k + 2], t[k + 1]);
            r.Sections.Add(new RefSection
            {
                Material = mat, Tex = tex, Pos = pos.Select(Place).ToArray(), Normal = nrm.Select(Turn).ToArray(), Uv = uv, Tris = t,
                Weights = pos.Select(_ => new[] { (pelvis, 1f) }).ToArray(),
            });
        }
        // how far the mesh's arms are from the skeleton's (T pose against A pose …): each arm's farthest point out from the shoulder
        var placed = r.Sections.SelectMany(x => x.Pos).ToList();
        foreach (var s in new[] { "l", "r" })
        {
            int sh = sk.Find($"g_{s}_shoulder"), wr = sk.Find($"g_{s}_wrist");
            if (sh < 0 || wr < 0) continue;
            var shp = sk.Pos(sh);
            float side = MathF.Sign(Vector3.Dot(sk.Pos(wr) - pel, sk.Left));
            float shoulderOut = Vector3.Dot(shp - pel, sk.Left) * side;
            var arm = placed.Where(p => Vector3.Dot(p - pel, sk.Left) * side > shoulderOut + 0.1f * sk.Height).ToList();
            if (arm.Count == 0) { notes.Add($"{(s == "l" ? "left" : "right")} arm: none found out past the shoulder"); continue; }
            var tip = arm.MaxBy(p => Vector3.DistanceSquared(p, shp));
            var a = Vector3.Normalize(sk.Pos(wr) - shp); var b = Vector3.Normalize(tip - shp);
            notes.Add($"{(s == "l" ? "left" : "right")} arm: the mesh's points {MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1, 1)) * 180 / MathF.PI:0}° from the skeleton's (wrist {Vector3.Dot(sk.Pos(wr) - shp, sk.Up):0.#} up from the shoulder, mesh hand {Vector3.Dot(tip - shp, sk.Up):0.#})");
        }
        notes.Insert(0, $"no armature: {parts.Count} mesh(es), {all.Count:N0} vertices, {Axis(up)} up, {height:0.##} file units tall, scaled ×{scale:0.####} to the hero's height");
        return r;
    }

    static string Axis(Vector3 a) => a.X != 0 ? "X" : a.Y != 0 ? "Y" : "Z";

    /// <summary>Step 2: model.fbx (the fitted mesh with the hero's skeleton), then Blender binds it headless; the rigged FBX's
    /// path. Throws with a plain reason (no Blender, Blender failed).</summary>
    public static string Rig(string fbx, MhoSkeleton sk, string package, string folder, Action<string> log)
    {
        string exe = BlenderLaunch.Find() ?? throw new InvalidOperationException("an FBX without an armature is rigged in Blender, which isn't installed: pick blender.exe in Settings ▾ → Model → Choose Blender");
        Protected.CheckWrite(folder);
        Directory.CreateDirectory(folder);
        var notes = new List<string>();
        var r = Fit(fbx, sk, notes);
        foreach (var n in notes) log("  " + n);
        int pelvis = Math.Max(0, sk.Find("g_pelvis"));
        // the hero's own body rides along as a guide (Blender keeps each vertex to the bones the body uses there, then deletes it)
        if (sk.Mesh.HighestDetail is { } body && body.Positions.Count > 0)
        {
            var w = body.Influences.Select(vi => Enumerable.Range(0, vi.Bones.Count).Where(k => vi.Weights[k] > 0 && vi.Bones[k] >= 0 && vi.Bones[k] < sk.Bones.Count)
                .Select(k => (vi.Bones[k], vi.Weights[k])).ToArray()).ToArray();
            r.Sections.Add(new RefSection
            {
                Material = GuideName, Tex = new Textures(), Pos = body.Positions.ToArray(), Normal = body.Normals.ToArray(),
                Uv = body.TexCoords.Select(t => new Vector2(t.X, 1 - t.Y)).ToArray(), Tris = body.Indices.Select(i => (int)i).ToArray(),
                Weights = w.Select(x => x.Length > 0 ? x : [(pelvis, 1f)]).ToArray(),
            });
        }
        FbxExport.Run(r, package, folder, ["\u0001"], _ => { }, exact: true);
        // the bones the hero's own body is weighted to: the rest (root, IK, offsets, capes, props) take no weights
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sk.Mesh.HighestDetail is { } lod)
            foreach (var vi in lod.Influences)
                for (int k = 0; k < vi.Bones.Count; k++) if (vi.Weights[k] > 0 && vi.Bones[k] >= 0 && vi.Bones[k] < sk.Bones.Count) used.Add(sk.Bones[vi.Bones[k]].Name);
        // nor do the face's detail bones and the hero's own hair / cape / cloth chains: they sit inside the head and the body, so heat
        // weighting gave them a foreign model's skull and hair (64 % of everything above g_head went to eyes, eyelids and jaw, 6 %
        // to the head: the skull showed through the hair when they moved; Kurt, Carter on Agent 13). The head takes it all.
        used.RemoveWhere(b => FaceOrChain.IsMatch(b));
        // every finger joint, though: some heroes' own hands use only a few (Captain America's: thumbs, index1, birdy1), and a
        // model with five full fingers then couldn't curl the rest (Kurt, Carter on Captain America)
        foreach (var b in sk.Bones) if (Finger.IsMatch(b.Name)) used.Add(b.Name);
        File.WriteAllLines(Path.Combine(folder, "deform_bones.txt"), used.OrderBy(x => x));
        string script = Path.Combine(folder, "rig.py");
        File.WriteAllText(script, RigScript.Replace("@@FOLDER@@", folder));
        File.WriteAllText(Path.Combine(folder, "save_hook.py"), HookScript.Replace("@@FOLDER@@", folder));
        string rigged = RiggedFbx(folder);
        if (File.Exists(rigged)) File.Delete(rigged);
        var psi = new ProcessStartInfo(exe, $"-b --python \"{script}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Blender didn't start");
        var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10 * 60_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new InvalidOperationException("Blender took over 10 minutes to rig the model"); }
        string output = stdout.Result + stderr.Result;
        foreach (var line in output.Split('\n').Where(l => l.StartsWith("MHO rig:")).Select(l => l.Trim())) log("  " + line["MHO rig:".Length..].Trim());
        if (!File.Exists(rigged))
            throw new InvalidOperationException("Blender couldn't rig the model: " + string.Join(" | ", output.Split('\n').Where(l => l.Contains("Error") || l.Contains("MHO rig")).Select(l => l.Trim()).Take(6)));
        if (Environment.GetEnvironmentVariable("MHO_RIG_KEEP") != "1") File.Delete(Path.Combine(folder, "model.fbx"));   // only the rig is kept (4–5 MB less in the mod; MHO_RIG_KEEP=1: tests)
        return rigged;
    }

    /// <summary>Opens rig.blend in Blender with the save hook (each Ctrl+S exports rigged.fbx again). Null = started; else why not.</summary>
    public static string? Open(string folder)
    {
        string? exe = BlenderLaunch.Find();
        if (exe == null) return "Blender isn't installed: pick blender.exe in Settings ▾ → Model → Choose Blender.";
        string blend = Path.Combine(folder, "rig.blend");
        if (!File.Exists(blend)) return "the rig hasn't been made yet (pick a base hero first)";
        File.WriteAllText(Path.Combine(folder, "save_hook.py"), HookScript.Replace("@@FOLDER@@", folder));
        Process.Start(new ProcessStartInfo(exe, $"\"{blend}\" --python \"{Path.Combine(folder, "save_hook.py")}\"") { UseShellExecute = false, WorkingDirectory = Settings.Home });   // not the rig folder: a running program's working folder can't be deleted (Clean Up)
        return null;
    }

    /// <summary>Shared by both scripts: the export the tab reads (the same settings as the Blender round trip's model export).</summary>
    const string NL = "\n";   // raw strings drop their last newline

    const string ExportPy = """
def mho_export_rigged():
    import bpy, os
    # the rig's armature (tagged when it was made), not one the user's startup scene brought along
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    arm = (next((o for o in arms if o.get("mho_model")), None) or next((o for o in arms if "g_pelvis" in o.data.bones), None)
           or (arms[0] if arms else None))
    if arm is None:
        print("MHO rig: no armature in the scene")
        return False
    sel = (list(bpy.context.selected_objects), bpy.context.view_layer.objects.active)
    mode = bpy.context.object.mode if bpy.context.object else "OBJECT"
    if mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.select_all(action="DESELECT")
    arm.select_set(True)
    for o in bpy.data.objects:
        if o.type == "MESH" and (o.parent == arm or any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers)):
            o.select_set(True)
    bpy.context.view_layer.objects.active = arm
    # beside the .blend (the rig moves from the editor's work folder into the mod's Model folder on Save)
    folder = os.path.dirname(bpy.data.filepath) or FOLDER
    out = os.path.join(folder, "rigged.fbx")
    tmp = os.path.join(folder, "rigged.tmp.fbx")
    bpy.ops.export_scene.fbx(filepath=tmp, check_existing=False, use_selection=True, object_types={"ARMATURE", "MESH"},
        use_custom_props=False, global_scale=1.0, apply_unit_scale=True, apply_scale_options="FBX_SCALE_NONE",
        use_space_transform=True, bake_space_transform=True, axis_forward="-Z", axis_up="Y", bake_anim=False,
        add_leaf_bones=False, path_mode="AUTO", batch_mode="OFF")
    os.replace(tmp, out)
    bpy.ops.object.select_all(action="DESELECT")
    for o in sel[0]:
        o.select_set(True)
    bpy.context.view_layer.objects.active = sel[1]
    return True
""";

    /// <summary>Headless: import, bind with Automatic Weights, the rest to the nearest bone, save rig.blend, export.</summary>
    const string RigScript = """
# Written by the MHO Extended Mod Manager (Model tab): rigs an FBX that had no armature to the base hero's skeleton.
import bpy, os
from mathutils.geometry import intersect_point_line
FOLDER = r"@@FOLDER@@"
""" + NL + ExportPy + NL + """

# into the user's own startup scene (Kurt, 2026-10-04: his default scene as the base); only what's imported is worked on
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=os.path.join(FOLDER, "model.fbx"))
new = [o for o in bpy.data.objects if o not in before]
arm = next(o for o in new if o.type == "ARMATURE")
arm["mho_model"] = 1
guide = next((o for o in new if o.type == "MESH" and o.name.upper().startswith("MHO_STOCK_GUIDE")), None)
meshes = [o for o in new if o.type == "MESH" and o != guide]
deform = set(l.strip().lower() for l in open(os.path.join(FOLDER, "deform_bones.txt"), encoding="utf-8") if l.strip())
for b in arm.data.bones:
    b.use_deform = b.name.lower() in deform
print("MHO rig: %d of %d bones take weights (the ones the hero's own body uses)" % (sum(b.use_deform for b in arm.data.bones), len(arm.data.bones)))
for m in meshes:
    mw = m.matrix_world.copy()
    m.parent = None
    m.matrix_world = mw
    m.vertex_groups.clear()
    for mod in list(m.modifiers):
        if mod.type == "ARMATURE":
            m.modifiers.remove(mod)
# Heat weighting needs one connected surface: game meshes are split at every UV seam and hard edge (each triangle island
# its own piece), and on those it finds no solution at all. So it runs on a welded copy (all the meshes joined, vertices at
# the same place merged), and each vertex then takes the weights of its twin there.
import bmesh
from mathutils.kdtree import KDTree
size = max(max(m.dimensions) for m in meshes) or 1.0
weld = bpy.data.objects.new("mho_weld", bpy.data.meshes.new("mho_weld"))
bpy.context.scene.collection.objects.link(weld)
bm = bmesh.new()
for m in meshes:
    t = bmesh.new()
    t.from_mesh(m.data)
    t.transform(m.matrix_world)
    tmp = bpy.data.meshes.new("t")
    t.to_mesh(tmp)
    bm.from_mesh(tmp)
    bpy.data.meshes.remove(tmp)
    t.free()
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=size * 1e-4)
bm.to_mesh(weld.data)
bm.free()
bpy.ops.object.select_all(action="DESELECT")
weld.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
# Heat weighting goes by distance to the bones. Some heroes' finger bones are curled into a fist inside their own palm
# (Captain America's), far from a model's straight fingers, which then got no ring, pinky or second-joint weights and
# couldn't curl (Kurt, Carter on Captain America). So for the weighting only, each finger chain is laid out straight from its
# first knuckle along the hand (the thumb along its own first bone); the bones go back exactly afterwards, so the skeleton the
# game animates is unchanged.
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="EDIT")
eb = arm.data.edit_bones
saved = {}
straightened = 0
# First the arms: a model's arms can hang at another angle than the hero's (Carter on Captain America: 7 degrees, her hands 6
# units below his wrists), which leaves the hero's hand bones beside the model's hands. Each arm chain is turned (and scaled,
# within 20 %) about its shoulder so its farthest joint lands on the model's farthest point out past that shoulder.
from mathutils import Vector
# (in the armature's own space, as edit bones are: the FBX import scales and turns the armature object)
to_arm = arm.matrix_world.inverted()
verts = [to_arm @ (m.matrix_world @ v.co) for m in meshes for v in m.data.vertices]
pb, hb2 = eb.get("g_pelvis"), eb.get("g_head")
height = 0.0
if pb is not None and hb2 is not None and verts:
    upv = (hb2.head - pb.head)
    upv.normalize()
    hs = [p.dot(upv) for p in verts]
    height = max(hs) - min(hs)
ls, rs = eb.get("g_l_shoulder"), eb.get("g_r_shoulder")
turned = []
if ls is not None and rs is not None and height > 0:
    across = (ls.head - rs.head)
    across.normalize()
    mid = (ls.head + rs.head) / 2
    for sh, sign in ((ls, 1), (rs, -1)):
        out = (sh.head - mid).dot(across) * sign
        arm_pts = [p for p in verts if (p - mid).dot(across) * sign > out + 0.1 * height]
        chain = [sh] + list(sh.children_recursive)
        if not arm_pts or len(chain) < 3:
            continue
        tip = max(arm_pts, key=lambda p: (p - sh.head).length)
        # (by the bones that take weights: an attach point or a prop bone on the arm can reach farther, and turned Storm's arms
        # 34 and 60 degrees)
        dchain = [b for b in chain if b.use_deform] or chain
        far = max((b.tail for b in dchain), key=lambda q: (q - sh.head).length)
        a, b2 = far - sh.head, tip - sh.head
        if a.length < 1e-6 or b2.length < 1e-6:
            continue
        rot = a.rotation_difference(b2)
        k = max(0.8, min(1.25, b2.length / a.length))
        S = sh.head.copy()
        for b in chain:
            if b.name not in saved:
                saved[b.name] = (b.head.copy(), b.tail.copy(), b.roll, b.use_connect)
            b.use_connect = False
        for b in chain:
            h, t = saved[b.name][0], saved[b.name][1]
            b.head = S + (rot @ (h - S)) * k
            b.tail = S + (rot @ (t - S)) * k
        turned.append("%s %.0f deg x%.2f" % (sh.name, a.angle(b2) * 57.2958, k))
if turned:
    print("MHO rig: arms lined up with the model's for the weighting: " + ", ".join(turned))
for side in ("l", "r"):
    wrist = eb.get("g_%s_wrist" % side)
    if wrist is None:
        continue
    knuckles = [eb.get("g_%s_%s1" % (side, f)) for f in ("index", "birdy", "ring", "pinky")]
    knuckles = [k for k in knuckles if k is not None]
    if not knuckles:
        continue
    hand = sum((k.head - wrist.head for k in knuckles), knuckles[0].head * 0) / len(knuckles)
    if hand.length < 1e-6:
        continue
    hand.normalize()
    for f in ("thumb", "index", "birdy", "ring", "pinky"):
        chain = [eb.get("g_%s_%s%d" % (side, f, k)) for k in (1, 2, 3)]
        chain = [b for b in chain if b is not None]
        if not chain:
            continue
        d = hand
        if f == "thumb":
            d = chain[0].tail - chain[0].head
            if d.length < 1e-6:
                continue
            d = d.normalized()
        p = chain[0].head.copy()
        for b in chain:
            if b.name not in saved:   # (the arm step may have saved it already: the originals are what goes back)
                saved[b.name] = (b.head.copy(), b.tail.copy(), b.roll, b.use_connect)
            b.use_connect = False
        for b in chain:
            n = (b.tail - b.head).length
            b.head = p
            b.tail = p + d * n
            p = b.tail.copy()
        straightened += 1
bpy.ops.object.mode_set(mode="OBJECT")
bpy.ops.object.select_all(action="DESELECT")
weld.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.parent_set(type="ARMATURE_AUTO")
if saved:
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="EDIT")
    for n, (h, t, r, c) in saved.items():
        b = arm.data.edit_bones[n]
        b.head, b.tail, b.roll = h, t, r
    for n, (h, t, r, c) in saved.items():
        arm.data.edit_bones[n].use_connect = c
    bpy.ops.object.mode_set(mode="OBJECT")
    print("MHO rig: %d finger chains laid straight for the weighting, then put back" % straightened)
print("MHO rig: welded copy %d vertices (from %d)" % (len(weld.data.vertices), sum(len(m.data.vertices) for m in meshes)))
tree = KDTree(len(weld.data.vertices))
for v in weld.data.vertices:
    tree.insert(weld.matrix_world @ v.co, v.index)
tree.balance()
names = {g.index: g.name for g in weld.vertex_groups}
for m in meshes:
    for v in m.data.vertices:
        _, i, _ = tree.find(m.matrix_world @ v.co)
        for g in weld.data.vertices[i].groups:
            if g.weight > 0:
                vg = m.vertex_groups.get(names[g.group]) or m.vertex_groups.new(name=names[g.group])
                vg.add([v.index], g.weight, "REPLACE")
bpy.data.objects.remove(weld)
bpy.ops.object.select_all(action="DESELECT")
for m in meshes:
    m.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.parent_set(type="ARMATURE")

# vertices heat weighting didn't reach (loose pieces, gaps): the nearest bone, whole
segs = [(b.name, arm.matrix_world @ b.head_local, arm.matrix_world @ b.tail_local) for b in arm.data.bones if b.use_deform]
def nearest(p):
    best, name = None, None
    for n, h, t in segs:
        q, f = intersect_point_line(p, h, t)
        if f < 0: q = h
        elif f > 1: q = t
        d = (p - q).length
        if best is None or d < best:
            best, name = d, n
    return name
# Loose pieces heat weighting didn't reach (a pouch on a belt) take the weights of the nearest vertex that has some (the
# belt), not of their nearest bone (in the bind pose a hand hangs by the hips); the nearest bone only when nothing is weighted.
weighted = []
for m in meshes:
    gname = {g.index: g.name for g in m.vertex_groups}
    for v in m.data.vertices:
        w = [(gname[g.group], g.weight) for g in v.groups if g.weight > 0]
        if w:
            weighted.append((m.matrix_world @ v.co, w))
wtree = KDTree(len(weighted))
for i, (p, _) in enumerate(weighted):
    wtree.insert(p, i)
wtree.balance()
fixed = 0
bone = 0
total = 0
for m in meshes:
    total += len(m.data.vertices)
    for v in m.data.vertices:
        if any(g.weight > 0 for g in v.groups):
            continue
        p = m.matrix_world @ v.co
        if weighted:
            _, i, _ = wtree.find(p)
            for n, w in weighted[i][1]:
                vg = m.vertex_groups.get(n) or m.vertex_groups.new(name=n)
                vg.add([v.index], w, "REPLACE")
            fixed += 1
        else:
            n = nearest(p)
            vg = m.vertex_groups.get(n) or m.vertex_groups.new(name=n)
            vg.add([v.index], 1.0, "REPLACE")
            bone += 1
print("MHO rig: automatic weights on %d of %d vertices; %d more (loose pieces) took their nearest weighted neighbour's%s"
      % (total - fixed - bone, total, fixed, "; %d their nearest bone" % bone if bone else ""))
# The hero's own body as a guide: heat weighting goes by distance to the bones, so a pouch by the hip takes weight from the
# hand hanging next to it. Each vertex keeps only the bones the body uses near it (its 8 nearest body vertices, with their
# bones' parents and children); a vertex left with none takes the nearest body vertex's weights.
if guide is not None:
    gnames = {g.index: g.name for g in guide.vertex_groups}
    # the body's weights on bones that take none here (eyes, its own hair …) count for their nearest parent that does (the head)
    def deform_of(name):
        b = arm.data.bones.get(name)
        while b is not None and not b.use_deform:
            b = b.parent
        return b.name if b is not None else None
    gverts = []
    for v in guide.data.vertices:
        w = {}
        for g in v.groups:
            d = deform_of(gnames[g.group]) if g.weight > 0.02 else None
            if d:
                w[d] = w.get(d, 0) + g.weight
        gverts.append((guide.matrix_world @ v.co, w))
    gtree = KDTree(len(gverts))
    for i, (p, _) in enumerate(gverts):
        gtree.insert(p, i)
    gtree.balance()
    near_bones = {}
    for b in arm.data.bones:
        s = {b.name}
        if b.parent:
            s.add(b.parent.name)
        s.update(c.name for c in b.children)
        near_bones[b.name] = s
    import re
    hands = []
    for side in ("l", "r"):
        hb = {b.name for b in arm.data.bones if b.use_deform and re.match(r"^g_%s_(wrist|thumb\d|index\d|birdy\d|ring\d|pinky\d)$" % side, b.name, re.I)}
        if hb:
            hands.append(hb)
    trimmed = 0
    guided = 0
    for m in meshes:
        gname = {g.index: g.name for g in m.vertex_groups}
        for v in m.data.vertices:
            p = m.matrix_world @ v.co
            allowed = set()
            for _, i, _ in gtree.find_n(p, 8):
                for n in gverts[i][1]:
                    allowed |= near_bones.get(n, {n})
            # a hand is one place: by the body's hand, every bone of that hand (the body's own fingers may use only a few)
            for hand in hands:
                if allowed & hand:
                    allowed |= hand
            drop = [g.group for g in v.groups if g.weight > 0 and gname[g.group] not in allowed]
            if not drop:
                continue
            for gi in drop:
                m.vertex_groups[gi].remove([v.index])
            trimmed += 1
            if not any(g.weight > 0 for g in v.groups):
                _, i, _ = gtree.find(p)
                for n, w in gverts[i][1].items():
                    vg = m.vertex_groups.get(n) or m.vertex_groups.new(name=n)
                    vg.add([v.index], w, "REPLACE")
                guided += 1
    print("MHO rig: the hero's body as a guide: %d vertices lost weights from bones the body doesn't use there (%d of them took the body's own)" % (trimmed, guided))
    bpy.data.objects.remove(guide)
# The skull and hair follow the head. Hair is its own shell running from the crown down the back, so heat weighting
# carried the neck's weight up through it (a third of the crown on g_neck: the skull showed through the hair when she
# bent; Kurt). Above the head joint, the weight on the neck and the bones below it moves to the head, fully over the
# first 40 % of the neck's length above the joint (no crease where the neck meets the head).
hb, nb = arm.data.bones.get("g_head"), arm.data.bones.get("g_neck")
if hb is not None and nb is not None:
    up = (arm.matrix_world @ hb.head_local) - (arm.matrix_world @ nb.head_local)
    span = up.length * 0.4
    up.normalize()
    hp = arm.matrix_world @ hb.head_local
    head_set = {b.name for b in hb.children_recursive} | {hb.name}
    moved = 0
    for m in meshes:
        gname = {g.index: g.name for g in m.vertex_groups}
        for v in m.data.vertices:
            above = (m.matrix_world @ v.co - hp).dot(up)
            if above <= 0:
                continue
            f = min(1.0, above / span) if span > 0 else 1.0
            take = 0.0
            for g in v.groups:
                if g.weight > 0 and gname[g.group] not in head_set:
                    take += g.weight * f
                    m.vertex_groups[g.group].add([v.index], g.weight * (1 - f), "REPLACE")
            if take > 0:
                vg = m.vertex_groups.get(hb.name) or m.vertex_groups.new(name=hb.name)
                cur = next((g.weight for g in v.groups if g.group == vg.index), 0.0)
                vg.add([v.index], cur + take, "REPLACE")
                moved += 1
    print("MHO rig: %d vertices above the head joint moved their neck / body weight to the head" % moved)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(FOLDER, "rig.blend"))
print("MHO rig: exported" if mho_export_rigged() else "MHO rig: export failed")
""";

    /// <summary>Interactive: each save exports rigged.fbx again (the tab watches it).</summary>
    const string HookScript = """
# Written by the MHO Extended Mod Manager (Model tab): every Ctrl+S sends the rig back (rigged.fbx).
import bpy
from bpy.app.handlers import persistent
FOLDER = r"@@FOLDER@@"
""" + NL + ExportPy + NL + """

@persistent
def mho_rig_on_save(*_):
    if mho_export_rigged():
        print("MHO rig: sent rigged.fbx")

for h in list(bpy.app.handlers.save_post):
    if getattr(h, "__name__", "") == "mho_rig_on_save":
        bpy.app.handlers.save_post.remove(h)
bpy.app.handlers.save_post.append(mho_rig_on_save)
""";
}
