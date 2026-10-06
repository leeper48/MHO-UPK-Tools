using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Opens an Export FBX folder in Blender (0.16.2, Kurt: "automatically load in a new Blender scene"): a script written into
/// the folder (open_in_blender.py, so it can be run again by hand) starts from an empty scene, imports model.fbx, turns
/// anims\ into Actions with the MHO Actions add-on's own FBX to Actions (anim.blend in that folder, made by a background
/// Blender as the add-on does) and pushes them to the armature's NLA with its Actions to NLA (a Root pose track first), then
/// saves model.blend beside model.fbx (the add-on's Batch Export writes next to the open .blend). Without the add-on it does
/// the same with Blender's own FBX import. The game folder and the MFF source are never touched: only the export folder.
/// </summary>
static class BlenderLaunch
{
    /// <summary>The Blender to use: the setting, else the newest installed one that has the MHO Actions add-on, else the newest.</summary>
    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("MFF_BLENDER_NONE") == "1") return null;   // tests: as on a PC without Blender
        if (Settings.Current.BlenderPath is string set && File.Exists(set)) return set;
        var installs = new List<(Version Ver, string Exe)>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string bf = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(bf)) continue;
            foreach (var d in Directory.GetDirectories(bf, "Blender*"))
                if (File.Exists(Path.Combine(d, "blender.exe")) && Regex.Match(Path.GetFileName(d), @"(\d+)\.(\d+)") is { Success: true } mm)
                    installs.Add((new Version(int.Parse(mm.Groups[1].Value), int.Parse(mm.Groups[2].Value)), Path.Combine(d, "blender.exe")));
        }
        if (installs.Count == 0) return null;
        var withAddon = installs.Where(i => HasAddon(i.Ver)).OrderByDescending(i => i.Ver).ToList();
        return (withAddon.Count > 0 ? withAddon[0] : installs.OrderByDescending(i => i.Ver).First()).Exe;
    }

    /// <summary>The Blender version of <paramref name="exe"/> (from its folder, "Blender 5.1"); null when it can't tell.</summary>
    public static Version? VersionOf(string exe) =>
        Regex.Match(Path.GetFileName(Path.GetDirectoryName(exe)) ?? "", @"(\d+)\.(\d+)") is { Success: true } m ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : null;

    /// <summary>The add-on's oldest Blender (its blender_manifest.toml: blender_version_min 5.0.0).</summary>
    public static readonly Version AddonMinimum = new(5, 0);

    /// <summary>The MHO Actions add-on zip shipped with the app (Assets\Blender, newest), or null.</summary>
    public static string? BundledAddon()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Blender");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "mho_fbx_to_actions-*.zip").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault() : null;
    }

    /// <summary>Whether to offer the add-on for <paramref name="exe"/>: that Blender lacks it, can run it (5.0+), and the app
    /// ships it.</summary>
    public static bool CanOfferAddon(string exe) => VersionOf(exe) is { } v && v >= AddonMinimum && !HasAddon(v) && BundledAddon() != null;

    /// <summary>
    /// Installs the shipped add-on into that Blender's user extensions and enables it, with Blender's own command
    /// (blender --command extension install-file -r user_default -e &lt;zip&gt;; Blender 4.2+). Not with --factory-startup:
    /// that saves factory preferences over the user's (seen on a scratch profile); without it their preferences stay (checked:
    /// a custom UI scale kept). Returns null when it's installed afterwards, else what Blender said.
    /// </summary>
    public static string? InstallAddon(string exe)
    {
        string? zip = BundledAddon();
        if (zip == null) return "the add-on isn't shipped with this app";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "--command", "extension", "install-file", "-r", "user_default", "-e", zip }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outText = p.StandardOutput.ReadToEndAsync(); var errText = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return "Blender didn't finish within 3 minutes"; }
        string said = (outText.Result + errText.Result).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("STATUS") || l.StartsWith("ERROR") || l.Contains("rror")) ?? "";
        return VersionOf(exe) is { } v && HasAddon(v) ? null : said.Length > 0 ? said : $"exit code {p.ExitCode}";
    }

    /// <summary>"Blender 5.1, with the MHO Actions add-on" (or without it) for the Blender at <paramref name="exe"/>.</summary>
    public static string Describe(string exe)
    {
        string dir = Path.GetFileName(Path.GetDirectoryName(exe)) ?? exe;
        var v = VersionOf(exe);
        bool addon = v != null && HasAddon(v);
        return $"{dir}, {(addon ? "with the MHO Actions add-on" : v != null && v < AddonMinimum ? "without the MHO Actions add-on (it needs Blender 5.0 or newer; the scene is built with Blender's own FBX import)" : "without the MHO Actions add-on (the scene is built with Blender's own FBX import: no anim.blend, no Root track)")}";
    }

    /// <summary>The MHO Actions add-on installed for that Blender version (an extension or a legacy add-on folder).</summary>
    public static bool HasAddon(Version v)
    {
        // Blender's own override of the user folder (BLENDER_USER_RESOURCES: no version folder under it), else AppData
        string user = Environment.GetEnvironmentVariable("BLENDER_USER_RESOURCES") is { Length: > 0 } ur ? ur
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender", $"{v.Major}.{v.Minor}");
        if (Directory.Exists(Path.Combine(user, "scripts", "addons", "mho_fbx_to_actions"))) return true;
        string ext = Path.Combine(user, "extensions");
        return Directory.Exists(ext) && Directory.GetDirectories(ext).Any(d => Directory.Exists(Path.Combine(d, "mho_fbx_to_actions")));
    }

    /// <summary>Writes open_in_blender.py into <paramref name="folder"/> (an Export FBX folder) and returns its path.</summary>
    public static string WriteScript(string folder)
    {
        Protected.CheckWrite(folder);
        string path = Path.Combine(folder, "open_in_blender.py");
        // a raw string in the script: backslashes as they are; the sync code goes in as a Python string literal (repr-safe:
        // it holds no triple single quotes)
        File.WriteAllText(path, Script.Replace("@@SYNC@@", Sync).Replace("@@FOLDER@@", folder));
        return path;
    }

    /// <summary>Starts Blender on the folder (the script runs once its window is up). Null = started; else why not.</summary>
    public static string? Open(string folder)
    {
        string? exe = Find();
        if (exe == null) return "Blender isn't installed (no Blender Foundation folder in Program Files): pick blender.exe in Settings ▾ → Model → Choose Blender.";
        string script = WriteScript(folder);
        Process.Start(new ProcessStartInfo(exe, $"--python \"{script}\"") { UseShellExecute = false, WorkingDirectory = folder });
        return null;
    }

    /// <summary>The script (Blender 4.x / 5.x). @@FOLDER@@ = the export folder.</summary>
    const string Script = """
# Written by the MHO MFF Importer: opens this Export FBX folder in a new Blender scene.
# Run again with:  blender --python open_in_blender.py
import bpy, os, importlib

FOLDER = r"@@FOLDER@@"
MODEL = os.path.join(FOLDER, "model.fbx")
ANIMS = os.path.join(FOLDER, "anims")
LIBRARY = os.path.join(ANIMS, "anim.blend")
BLEND = os.path.join(FOLDER, "model.blend")
SYNC = r'''@@SYNC@@'''


def addon():
    if os.environ.get("MFF_NO_ADDON") == "1":   # tests: as without the add-on
        return None
    for name in ("bl_ext.user_default.mho_fbx_to_actions", "mho_fbx_to_actions"):
        try:
            return importlib.import_module(name)
        except ImportError:
            pass
    for mod in list(__import__("sys").modules.values()):
        if getattr(mod, "__name__", "").endswith(".mho_fbx_to_actions") and hasattr(mod, "push_actions_to_nla"):
            return mod
    return None


def actions_without_addon():
    # Blender's own FBX import, one file at a time, as the add-on's FBX to Actions does
    made = []
    for f in sorted(os.listdir(ANIMS)) if os.path.isdir(ANIMS) else []:
        if not f.lower().endswith(".fbx"):
            continue
        before = set(bpy.context.scene.objects)
        bpy.ops.import_scene.fbx(filepath=os.path.join(ANIMS, f))
        new = [o for o in bpy.context.scene.objects if o not in before]
        act = next((o.animation_data.action for o in new if o.animation_data and o.animation_data.action), None)
        if act:
            act.name = os.path.splitext(f)[0]
            act.use_fake_user = True
            made.append(act)
        for o in new:
            bpy.data.objects.remove(o, do_unlink=True)
    bpy.data.orphans_purge(do_recursive=True)
    return made


def run():
    # into the user's own startup scene (Kurt, 2026-10-04: his default scene as the base): its objects, lights, camera and
    # world are left as they are; the imported armature is tagged so the sync finds it and nothing else
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=MODEL)
    new = [o for o in bpy.data.objects if o not in before]
    arm = next((o for o in new if o.type == "ARMATURE"), None)
    if arm is not None:
        arm["mho_model"] = 1
    mho = addon()
    log = []
    if arm is not None and os.path.isdir(ANIMS):
        if mho is not None:
            mho.run_fbx_to_actions_in_background(ANIMS, LIBRARY, False)
            actions = mho.append_actions_from_library(LIBRARY)
            if mho.armature_needs_root_track(arm):
                mho.create_root_pose_track(arm)
            mho.push_actions_to_nla(arm, actions, True, log)
            props = getattr(bpy.context.scene, "mho_props", None)
            if props is not None and hasattr(props, "target_armature"):
                props.target_armature = arm
        else:
            actions = actions_without_addon()
            ad = arm.animation_data or arm.animation_data_create()
            for act in actions:
                tr = ad.nla_tracks.new()
                tr.name = act.name
                tr.strips.new(act.name, int(act.frame_range[0]), act)
        bpy.ops.object.select_all(action="DESELECT")
        arm.select_set(True)
        bpy.context.view_layer.objects.active = arm
    # Ctrl+S sends the changes back to the importer (0.16.3): the sync code as a text block that registers itself when the
    # file opens (scripts allowed), run now for this session, with what the scene holds now as the baseline
    txt = bpy.data.texts.get("mff_sync.py") or bpy.data.texts.new("mff_sync.py")
    txt.clear()
    txt.write(SYNC)
    txt.use_module = True
    exec(compile(SYNC, "mff_sync.py", "exec"), {"__name__": "mff_sync"})
    bpy.app.driver_namespace["mff_sync_baseline"]()
    bpy.ops.wm.save_as_mainfile(filepath=BLEND)
    print(f"MHO MFF Importer: {os.path.basename(MODEL)} with {len(bpy.data.actions)} actions ({('MHO Actions add-on: ' + mho.__name__) if mho else 'without the add-on'}), saved {BLEND}")
    return None


if bpy.app.background:
    run()
else:
    bpy.app.timers.register(run, first_interval=0.5)
""";

    /// <summary>
    /// The Blender half of the roundtrip (0.16.3, Kurt: Ctrl+S in Blender sends the work back). On every save of the scene
    /// Open in Blender made: each action's keys and each mesh (vertices, vertex groups) are fingerprinted against the
    /// baseline taken when the scene was made; what differs and wasn't sent before is exported into from_blender\ with the
    /// MHO Actions add-on's game export settings (the mesh: model.fbx, no animation; an action: anims\&lt;name&gt;.fbx, only
    /// its NLA track and Root playing, over its strip's frames), and from_blender\sync.json lists everything changed so far
    /// with a counter. The importer watches that file. Layered (Blender 4.4+ / 5) and legacy actions both read.
    /// </summary>
    const string Sync = """
import bpy, os, json, hashlib, time
from bpy.app.handlers import persistent

FOLDER = r"@@FOLDER@@"
OUT = os.path.join(FOLDER, "from_blender")


def _fcurves(act):
    out = []
    for layer in getattr(act, "layers", []):
        for strip in layer.strips:
            for cb in getattr(strip, "channelbags", []):
                out.extend(cb.fcurves)
    if not out:
        try:
            out = list(act.fcurves)
        except Exception:
            pass
    return out


def _action_print(act):
    h = hashlib.md5()
    for fc in sorted(_fcurves(act), key=lambda f: (f.data_path, f.array_index)):
        h.update(f"{fc.data_path}[{fc.array_index}]".encode())
        for k in fc.keyframe_points:
            h.update(("%.5f %.5f %.5f %.5f %.5f %.5f;" % (k.co[0], k.co[1], k.handle_left[0], k.handle_left[1], k.handle_right[0], k.handle_right[1])).encode())
    return h.hexdigest()


def _armature():
    # the imported model's (tagged), not one the user's startup scene brought along
    arms = [o for o in bpy.data.objects if o.type == "ARMATURE"]
    return (next((o for o in arms if o.get("mho_model")), None) or next((o for o in arms if "g_pelvis" in o.data.bones), None)
            or (arms[0] if arms else None))


def _meshes(arm):
    return [o for o in bpy.data.objects if o.type == "MESH" and (o.parent == arm or any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers))]


def _mesh_print(arm):
    h = hashlib.md5()
    for o in sorted(_meshes(arm), key=lambda o: o.name):
        me = o.data
        h.update(f"{o.name}:{len(me.vertices)}:{len(me.polygons)}".encode())
        co = [0.0] * (len(me.vertices) * 3)
        me.vertices.foreach_get("co", co)
        h.update(",".join("%.4f" % c for c in co).encode())
        names = [g.name for g in o.vertex_groups]
        h.update("|".join(names).encode())
        for v in me.vertices:
            for g in v.groups:
                if g.weight > 0.0001:
                    h.update(("%d:%d:%.4f;" % (v.index, g.group, g.weight)).encode())
    return h.hexdigest()


def _prints():
    arm = _armature()
    tracks = arm.animation_data.nla_tracks if arm and arm.animation_data else []
    acts = {}
    for t in tracks:
        if t.name.lower() in ("root", "offset"):
            continue
        for st in t.strips:
            if st.action:
                acts[t.name] = _action_print(st.action)
    return {"mesh": _mesh_print(arm) if arm else "", "actions": acts, "rest": _rest_print(arm) if arm else ""}


def _rest_print(arm):
    # the bones' rest pose (Edit Mode): moved bones go out with the model (model.fbx carries the rest pose; Kurt, 2026-10-05:
    # bone edits from a Single Animation scene count too)
    h = hashlib.md5()
    for b in arm.data.bones:
        h.update(("%s %.4f %.4f %.4f %.4f %.4f %.4f;" % (b.name, *b.head_local, *b.tail_local)).encode())
    return h.hexdigest()


def _kwargs(**over):
    kw = dict(check_existing=False, use_selection=True, use_visible=False, use_active_collection=False,
              object_types={"ARMATURE", "MESH"}, use_custom_props=False, global_scale=1.0, apply_unit_scale=True,
              apply_scale_options="FBX_SCALE_NONE", use_space_transform=True, bake_space_transform=True,
              axis_forward="-Z", axis_up="Y", bake_anim=True, bake_anim_use_all_bones=True,
              bake_anim_use_nla_strips=False, bake_anim_use_all_actions=False, bake_anim_force_startend_keying=True,
              bake_anim_step=1.0, bake_anim_simplify_factor=1.0, path_mode="AUTO", batch_mode="OFF")
    kw.update(over)
    return kw


def _select(arm):
    bpy.ops.object.select_all(action="DESELECT")
    arm.select_set(True)
    for o in _meshes(arm):
        o.select_set(True)
    bpy.context.view_layer.objects.active = arm


def _export_track(arm, name, path):
    ad = arm.animation_data
    scene = bpy.context.scene
    tweak = ad.use_tweak_mode
    if tweak:
        ad.use_tweak_mode = False
    mutes = [(t, t.mute) for t in ad.nla_tracks]
    fs, fe = scene.frame_start, scene.frame_end
    active = ad.action
    try:
        ad.action = None
        target = None
        for t in ad.nla_tracks:
            keep = t.name.lower() in ("root", "offset") or t.name == name
            t.mute = not keep
            if t.name == name:
                target = t
        scene.frame_start = int(min(s.frame_start for s in target.strips))
        scene.frame_end = int(max(s.frame_end for s in target.strips))
        bpy.ops.export_scene.fbx(filepath=path, **_kwargs())
    finally:
        for t, m in mutes:
            t.mute = m
        scene.frame_start, scene.frame_end = fs, fe
        ad.action = active
        if tweak:
            ad.use_tweak_mode = True


@persistent
def mff_sync_on_save(*_):
    scene = bpy.context.scene
    # painted textures (Kurt, 2026-10-06: Texture Paint in this scene): saved to their files in the export folder, where the
    # importer picks them up as material maps
    for img in bpy.data.images:
        try:
            if img.is_dirty and img.source == "FILE" and img.filepath:
                img.save()
                print("MHO MFF Importer sync: saved", img.name)
        except Exception as ex:
            print("MHO MFF Importer sync: image not saved:", img.name, ex)
    base = json.loads(scene.get("mff_sync_baseline", "{}") or "{}")
    if not base:
        return
    now = _prints()
    arm = _armature()
    if arm is None:
        return
    state_path = os.path.join(OUT, "sync.json")
    state = json.load(open(state_path, encoding="utf-8")) if os.path.isfile(state_path) else {"version": 0, "model": None, "anims": {}}
    # what was sent already lives with the sync file (a scene property set after a save isn't in that save)
    sent = state.get("sent", {})
    todo_mesh = now["mesh"] != base.get("mesh") and now["mesh"] != sent.get("mesh")
    todo = [n for n, fp in now["actions"].items() if fp != base.get("actions", {}).get(n) and fp != sent.get("actions", {}).get(n)]
    todo_rest = "rest" in base and now["rest"] != base.get("rest") and now["rest"] != sent.get("rest")
    if not todo_mesh and not todo and not todo_rest:
        return
    os.makedirs(os.path.join(OUT, "anims"), exist_ok=True)
    changed = []
    if todo_rest:
        sent["rest"] = now["rest"]
        changed.append("bones")
    sel = (list(bpy.context.selected_objects), bpy.context.view_layer.objects.active)
    mode = bpy.context.object.mode if bpy.context.object else "OBJECT"
    try:
        if mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        _select(arm)
        if todo_mesh or todo_rest:
            bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, "model.fbx"), **_kwargs(bake_anim=False))
            state["model"] = "model.fbx"
            sent["mesh"] = now["mesh"]
            if todo_mesh:
                changed.append("the mesh")
        for name in todo:
            _export_track(arm, name, os.path.join(OUT, "anims", name + ".fbx"))
            state["anims"][name] = "anims/" + name + ".fbx"
            sent.setdefault("actions", {})[name] = now["actions"][name]
            changed.append(name)
    finally:
        bpy.ops.object.select_all(action="DESELECT")
        for o in sel[0]:
            o.select_set(True)
        bpy.context.view_layer.objects.active = sel[1]
        if mode != "OBJECT" and bpy.context.object:
            try:
                bpy.ops.object.mode_set(mode=mode)
            except Exception:
                pass
    if not changed:
        return
    state["version"] = int(state.get("version", 0)) + 1
    state["time"] = time.strftime("%Y-%m-%d %H:%M:%S")
    state["last"] = changed
    state["sent"] = sent
    tmp = state_path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=1)
    os.replace(tmp, state_path)
    print("MHO MFF Importer sync:", ", ".join(changed))


def mff_sync_baseline():
    bpy.context.scene["mff_sync_baseline"] = json.dumps(_prints())


for h in list(bpy.app.handlers.save_post):
    if getattr(h, "__name__", "") == "mff_sync_on_save":
        bpy.app.handlers.save_post.remove(h)
bpy.app.handlers.save_post.append(mff_sync_on_save)
bpy.app.driver_namespace["mff_sync_baseline"] = mff_sync_baseline
""";
}
