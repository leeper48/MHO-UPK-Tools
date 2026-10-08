using System.Diagnostics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Model files the Model tab reads through Blender (Kurt, 2026-10-06: "add OBJ, XPS, DAE, STL … Blend too"). OBJ, DAE and STL
/// are read directly (Assimp, like FBX). A .blend file and XNALara / XPS models (.xps, .mesh, .mesh.ascii: binary with or
/// without a header, or text) are turned into an FBX by Blender in the background, once (kept until the file changes), and
/// that FBX is the source. XPS has no importer in Blender itself: the script reads it (the XNALara layout, checked by reading
/// each file exactly to its end) and builds the armature, meshes, weights and materials; textures are copied beside the FBX.
/// </summary>
static class ModelConvert
{
    /// <summary>Every model file Single Model's Browse offers (the dialog's filter).</summary>
    public const string Filter = "Models (*.fbx;*.obj;*.dae;*.stl;*.blend;*.xps;*.mesh;*.ascii)|*.fbx;*.obj;*.dae;*.stl;*.blend;*.xps;*.mesh;*.ascii"
        + "|FBX (*.fbx)|*.fbx|OBJ (*.obj)|*.obj|Collada (*.dae)|*.dae|STL (*.stl)|*.stl|Blender (*.blend)|*.blend|XPS / XNALara (*.xps;*.mesh;*.mesh.ascii)|*.xps;*.mesh;*.ascii";

    /// <summary>The file goes through Blender first (.blend, .xps, .mesh, .mesh.ascii).</summary>
    public static bool NeedsBlender(string file)
    {
        string f = file.ToLowerInvariant();
        return f.EndsWith(".blend") || f.EndsWith(".xps") || f.EndsWith(".mesh") || f.EndsWith(".mesh.ascii") || f.EndsWith(".ascii");
    }

    /// <summary>A model without bones by nature (STL): never an MFF character.</summary>
    public static bool IsStl(string file) => file.EndsWith(".stl", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name a converted model goes by: the file's, or its folder's for XNALara's generic names (xps.xps,
    /// generic_item.mesh).</summary>
    public static string NameOf(string file)
    {
        string name = Path.GetFileName(file);
        foreach (var ext in new[] { ".mesh.ascii", ".mesh", ".xps", ".blend", ".ascii" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        while (name.EndsWith(".mesh.ascii", StringComparison.OrdinalIgnoreCase)) name = name[..^".mesh.ascii".Length];   // Generic_Item.mesh.ascii.mesh.ascii …
        if (name.Length == 0 || name.Equals("xps", StringComparison.OrdinalIgnoreCase) || name.StartsWith("generic_item", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file))) ?? "model";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    /// <summary>Where the FBX of <paramref name="file"/> goes: data\model\converted\&lt;hash of its path&gt;\&lt;its folder's
    /// name&gt;\&lt;name&gt;.fbx (the folder's name, as an MFF character's model is known by its folder).</summary>
    public static string FbxFor(string file)
    {
        string full = Path.GetFullPath(file);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant();
        string folder = Path.GetFileName(Path.GetDirectoryName(full)) is { Length: > 0 } d ? d : "model";
        return Path.Combine(Settings.Home, "converted", hash, folder, NameOf(file) + ".fbx");
    }

    /// <summary>The FBX of a .blend / XPS file (made by Blender when missing or older than the file); throws with the reason.</summary>
    public static string ToFbx(string file, Action<string> log)
    {
        string out_ = FbxFor(file);
        if (File.Exists(out_) && File.GetLastWriteTimeUtc(out_) > File.GetLastWriteTimeUtc(file)) { Record(out_, file); return out_; }
        string exe = BlenderLaunch.Find() ?? throw new InvalidOperationException("this file is read through Blender, and Blender isn't installed: pick blender.exe in Settings ▾ → Model → Choose Blender.");
        string dir = Path.GetDirectoryName(out_)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "convert.py");
        File.WriteAllText(script, Script);
        bool blend = file.EndsWith(".blend", StringComparison.OrdinalIgnoreCase);
        string args = blend ? $"-b \"{file}\" --python \"{script}\" -- \"{file}\" \"{out_}\""
                            : $"-b --factory-startup --python \"{script}\" -- \"{file}\" \"{out_}\"";
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Blender didn't start");
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(5 * 60_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new InvalidOperationException("Blender took over 5 minutes to read the file"); }
        string output = so.Result + se.Result;
        foreach (var line in output.Split('\n').Where(l => l.StartsWith("MHO convert:"))) log("  " + line["MHO convert:".Length..].Trim());
        File.Delete(script);
        // the model folder's other pictures too (a normal map the file doesn't name: Captain Carter's _N beside her _D)
        if (File.Exists(out_))
            foreach (var img in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(file))!)
                         .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)))
                if (!File.Exists(Path.Combine(dir, Path.GetFileName(img)))) File.Copy(img, Path.Combine(dir, Path.GetFileName(img)));
        if (!File.Exists(out_))
            throw new InvalidOperationException("Blender couldn't read it: " + string.Join(" | ", output.Split('\n').Where(l => l.Contains("Error") || l.Contains("MHO convert")).Select(l => l.Trim()).Take(6)));
        Record(out_, file);
        return out_;
    }

    /// <summary>The converted copy's source.txt (in its data\model\converted\&lt;hash&gt; folder): the file it was made from;
    /// written again on every use, so its date says when it was last used (<see cref="Prune"/>).</summary>
    static void Record(string fbx, string source)
    {
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(fbx)!)!, "source.txt"), Path.GetFullPath(source)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Converted copies no longer needed (a user, 2026-10-08: a .blend's FBX stayed in data\model\converted): its file is gone,
    /// or it hasn't been used for <paramref name="days"/> days and isn't in the Model tab's list of models. One without a
    /// source.txt (made before 0.37.219) goes after the same time unused. Each is made again from its file when picked.
    /// </summary>
    public static (int Files, long Bytes) Prune(int days)
    {
        string root = Path.Combine(Settings.Home, "converted");
        if (!Directory.Exists(root)) return (0, 0);
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var listed = Settings.Current.RecentFbx.Select(r => Path.GetFullPath(r)).ToList();
        int n = 0; long bytes = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            string rec = Path.Combine(dir, "source.txt");
            string? source = null;
            try { if (File.Exists(rec)) source = File.ReadAllText(rec).Trim(); } catch (IOException) { }
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
            string prefix = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            bool inList = listed.Any(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            DateTime used = File.Exists(rec) ? File.GetLastWriteTimeUtc(rec) : files.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            bool gone = source != null && !File.Exists(source);
            if (!gone && (inList || used >= cutoff)) continue;
            try
            {
                long size = files.Sum(f => new FileInfo(f).Length);
                Directory.Delete(dir, true);
                n += files.Count; bytes += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (n, bytes);
    }

    const string Script = """
import bpy, os, sys, struct, re

argv = sys.argv[sys.argv.index("--") + 1:]
SRC, OUT = argv[0], argv[1]
FOLDER = os.path.dirname(OUT)


def say(*a):
    print("MHO convert:", *a)


# --- XNALara / XPS ---------------------------------------------------------------------------------------------------------
class Bin:
    def __init__(self, data):
        self.d, self.p = data, 0
    def take(self, fmt):
        v = struct.unpack_from("<" + fmt, self.d, self.p)
        self.p += struct.calcsize("<" + fmt)
        return v if len(v) > 1 else v[0]
    def string(self):
        b1 = self.d[self.p]; self.p += 1
        b2 = 0
        if b1 >= 128:
            b2 = self.d[self.p]; self.p += 1
        n = (b1 % 128) + b2 * 128
        s = self.d[self.p:self.p + n].decode("utf-8", "replace"); self.p += n
        return s


def read_bin(path):
    data = open(path, "rb").read()
    f = Bin(data)
    tangents, variable = True, False
    if len(data) >= 4 and f.take("I") == 323232:
        major, minor = f.take("H"), f.take("H")
        f.string()                     # XNAaraL
        settings = f.take("I")
        f.string(); f.string(); f.string()   # machine, user, files
        tangents = major <= 12 and minor <= 2
        variable = major >= 3
        f.p += settings * 4            # settings (default pose, flags …): skipped whole
        say(f"XPS binary, version {major}.{minor}")
    else:
        f.p = 0
        say("XNALara .mesh (no header)")
    bones = []
    for _ in range(f.take("I")):
        name = f.string(); parent = f.take("h"); pos = f.take("3f")
        bones.append((name, parent, pos))
    meshes = []
    for _ in range(f.take("I")):
        name = f.string()
        layers = f.take("I")
        textures = []
        for _ in range(f.take("I")):
            textures.append((os.path.basename(f.string().replace("\\", "/")), f.take("I")))
        verts = []
        for _ in range(f.take("I")):
            pos = f.take("3f"); nrm = f.take("3f"); f.take("4B")
            uvs = []
            for _ in range(layers):
                uvs.append(f.take("2f"))
                if tangents:
                    f.take("4f")
            idx, w = (), ()
            if bones:
                n = f.take("h") if variable else 4
                idx = f.take(f"{n}h") if n else ()
                w = f.take(f"{n}f") if n else ()
                if n == 1:
                    idx, w = (idx,), (w,)
            verts.append((pos, nrm, uvs, idx, w))
        tris = [f.take("3I") for _ in range(f.take("I"))]
        meshes.append((name, textures, verts, tris))
    rest = data[f.p:]
    # what follows the meshes: nothing, or XNALara's footer (short strings "B", "O", "MM" …: 97 bytes on every sample)
    footer = rest[:2] == b"B"
    say(f"read {f.p:,} of {len(data):,} bytes" + ("" if not rest else " (then XNALara's footer)" if footer else " (NOT to the end: the layout may be wrong)"))
    return bones, meshes


def read_ascii(path):
    lines = []
    for raw in open(path, encoding="utf-8", errors="replace"):
        s = raw.split("#", 1)[0].strip()
        if s:
            lines.append(s)
    it = iter(lines)
    nxt = lambda: next(it)
    nums = lambda: [float(x) for x in nxt().split()]
    bones = []
    for _ in range(int(nxt().split()[0])):
        name = nxt(); parent = int(nxt().split()[0]); pos = tuple(nums()[:3])
        bones.append((name, parent, pos))
    meshes = []
    for _ in range(int(nxt().split()[0])):
        name = nxt()
        layers = int(nxt().split()[0])
        textures = []
        for _ in range(int(nxt().split()[0])):
            tname = os.path.basename(nxt().replace("\\", "/")); textures.append((tname, int(nxt().split()[0])))
        verts = []
        for _ in range(int(nxt().split()[0])):
            pos = tuple(nums()[:3]); nrm = tuple(nums()[:3]); nxt()   # colour
            uvs = [tuple(nums()[:2]) for _ in range(layers)]
            idx, w = (), ()
            if bones:
                idx = tuple(int(float(x)) for x in nxt().split()); w = tuple(nums())
            verts.append((pos, nrm, uvs, idx, w))
        tris = [tuple(int(x) for x in nxt().split()[:3]) for _ in range(int(nxt().split()[0]))]
        meshes.append((name, textures, verts, tris))
    left = sum(1 for _ in it)
    say(f"XPS text: {len(lines):,} lines" + ("" if left == 0 else f", {left} left over (the layout may be wrong)"))
    return bones, meshes


def z_up(v):
    return (v[0], -v[2], v[1])


def find_texture(name):
    stem = os.path.splitext(name)[0]
    files = {f.lower(): f for f in os.listdir(os.path.dirname(SRC))}
    for ext in (".png", ".jpg", ".jpeg", ".bmp", ".tga", os.path.splitext(name)[1].lower()):
        hit = files.get((stem + ext).lower())
        if hit:
            return os.path.join(os.path.dirname(SRC), hit)
    return None


NORMAL = re.compile(r"(_n|_nm|_nrm|_norm|_normal|_bump)$", re.I)


def build_xps(bones, meshes):
    arm_obj = None
    if bones:
        arm = bpy.data.armatures.new("Armature")
        arm_obj = bpy.data.objects.new("Armature", arm)
        bpy.context.scene.collection.objects.link(arm_obj)
        bpy.context.view_layer.objects.active = arm_obj
        bpy.ops.object.mode_set(mode="EDIT")
        heads = [z_up(b[2]) for b in bones]
        lo = [min(h[i] for h in heads) for i in range(3)]; hi = [max(h[i] for h in heads) for i in range(3)]
        tip = max(1e-3, max(hi[i] - lo[i] for i in range(3)) * 0.02)
        kids = {}
        for i, b in enumerate(bones):
            kids.setdefault(b[1], []).append(i)
        ebs = []
        for i, (name, parent, pos) in enumerate(bones):
            eb = arm.edit_bones.new(name)
            eb.head = heads[i]
            ch = kids.get(i, [])
            t = heads[ch[0]] if len(ch) == 1 else None
            if t is not None and sum((t[k] - heads[i][k]) ** 2 for k in range(3)) > 1e-8:
                eb.tail = t
            else:
                eb.tail = (heads[i][0], heads[i][1], heads[i][2] + tip)
            ebs.append(eb)
        for i, (name, parent, pos) in enumerate(bones):
            if 0 <= parent < len(ebs) and parent != i:
                ebs[i].parent = ebs[parent]
        bpy.ops.object.mode_set(mode="OBJECT")
    mats = {}
    flipped = 0; total = 0
    for name, textures, verts, tris in meshes:
        me = bpy.data.meshes.new(name)
        pos = [z_up(v[0]) for v in verts]
        # winding: XNALara's faces against the stored normals (majority vote); reversed when they point inward
        votes = 0
        for a, b, c in tris[:2000]:
            pa, pb, pc = pos[a], pos[b], pos[c]
            u = [pb[k] - pa[k] for k in range(3)]; w = [pc[k] - pa[k] for k in range(3)]
            fn = (u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0])
            vn = [sum(z_up(verts[i][1])[k] for i in (a, b, c)) for k in range(3)]
            votes += 1 if sum(fn[k] * vn[k] for k in range(3)) >= 0 else -1
        faces = tris if votes >= 0 else [(a, c, b) for a, b, c in tris]
        total += 1; flipped += votes < 0
        me.from_pydata(pos, [], faces)
        if verts and verts[0][2]:
            uv = me.uv_layers.new(name="UVMap")
            for poly in me.polygons:
                for li in poly.loop_indices:
                    u_, v_ = verts[me.loops[li].vertex_index][2][0]
                    uv.data[li].uv = (u_, 1.0 - v_)
        me.update()
        for poly in me.polygons:
            poly.use_smooth = True
        obj = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(obj)
        # material: named after the first (colour) texture, its file in the model's folder; a normal map beside it
        tex = [t[0] for t in textures]
        key = os.path.splitext(tex[0])[0] if tex else name
        mat = mats.get(key)
        if mat is None:
            mat = bpy.data.materials.new(key); mat.use_nodes = True
            nt = mat.node_tree; bsdf = nt.nodes.get("Principled BSDF")
            col = find_texture(tex[0]) if tex else None
            if col and bsdf:
                n = nt.nodes.new("ShaderNodeTexImage"); n.image = bpy.data.images.load(col)
                nt.links.new(n.outputs["Color"], bsdf.inputs["Base Color"])
            for t in tex[1:]:
                if NORMAL.search(os.path.splitext(t)[0]) and (nf := find_texture(t)) and bsdf:
                    n = nt.nodes.new("ShaderNodeTexImage"); n.image = bpy.data.images.load(nf)
                    n.image.colorspace_settings.name = "Non-Color"
                    nm = nt.nodes.new("ShaderNodeNormalMap")
                    nt.links.new(n.outputs["Color"], nm.inputs["Color"]); nt.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
                    break
            mats[key] = mat
            if tex and not col:
                say(f"texture {tex[0]} isn't in the model's folder")
        me.materials.append(mat)
        if arm_obj is not None:
            groups = {}
            for vi, v in enumerate(verts):
                for bi, bw in zip(v[3], v[4]):
                    if bw > 0 and 0 <= bi < len(bones):
                        g = groups.get(bi)
                        if g is None:
                            g = groups[bi] = obj.vertex_groups.new(name=bones[bi][0])
                        g.add([vi], bw, "ADD")
            obj.parent = arm_obj
            mod = obj.modifiers.new("Armature", "ARMATURE"); mod.object = arm_obj
    say(f"{len(bones)} bones, {len(meshes)} meshes, {sum(len(m[2]) for m in meshes):,} vertices, {sum(len(m[3]) for m in meshes):,} triangles"
        + (f"; faces turned outward on {flipped} of {total} meshes" if flipped else ""))


low = SRC.lower()
if low.endswith(".blend"):
    say(f"{os.path.basename(SRC)}: {sum(1 for o in bpy.data.objects if o.type == 'MESH')} meshes, {sum(1 for o in bpy.data.objects if o.type == 'ARMATURE')} armatures (Blender {bpy.app.version_string})")
else:
    bpy.ops.wm.read_factory_settings(use_empty=True)   # no startup cube, camera or light
    is_bin = low.endswith(".xps") or (low.endswith(".mesh") and not low.endswith(".ascii"))
    bones, meshes = read_bin(SRC) if is_bin else read_ascii(SRC)
    build_xps(bones, meshes)

bpy.ops.object.select_all(action="DESELECT")
for o in bpy.data.objects:
    if o.type in ("MESH", "ARMATURE") and o.name in bpy.context.view_layer.objects:
        o.hide_set(False)
        o.select_set(True)
bpy.ops.export_scene.fbx(filepath=OUT, use_selection=True, object_types={"ARMATURE", "MESH"}, use_mesh_modifiers=True,
                         add_leaf_bones=False, bake_anim=False, path_mode="COPY", embed_textures=False)
say("FBX written")
""";
}
