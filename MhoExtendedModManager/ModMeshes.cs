using System.Numerics;
using MhoPackageModifier;
using MhoPackageModifier.Gui;
using AnimPackage = AnimExportCli.Packages.Package;
using AnimExportCli.Meshes;
using InvalidPackageException = AnimExportCli.Packages.InvalidPackageException;

namespace MhoExtendedModManager;

/// <summary>A skeletal mesh in one of a mod's packages (the preview's 3D view).</summary>
sealed record MeshRef(string Package, string File, string Name, int Export)
{
    /// <summary>The preview key: "mesh:&lt;package file&gt;|&lt;mesh name&gt;".</summary>
    public string Key => $"mesh:{Package}|{Name}";
}

/// <summary>
/// The meshes a mod can show in the preview's 3D view (Kurt): the skeletal meshes in its character packages (costumes,
/// team-ups, NPCs, agents, pets), read with AnimExportCli's reader in their bind pose, with each section's colour
/// texture found through its material the way MHO Package Modifier's Meshes tab does (the material instance's texture
/// parameters, followed to its parents, else a base material's compiled textures; the texture's largest mip from the
/// package or the game's .tfc caches), and each section's material read for the preview's shading (ModMaterials to
/// ModelView.Look). The 3D view is the Mod Manager's own ModelView.
/// </summary>
static class ModMeshes
{
    static readonly string[] CharacterPrefixes = ["UC__MarvelPlayer_", "UC__MarvelTeamUp_", "UC__MarvelNPC_", "UC__MarvelAgent_", "UC__MarvelVanityPet_"];
    static int Rank(string file) { for (int i = 0; i < CharacterPrefixes.Length; i++) if (file.StartsWith(CharacterPrefixes[i], StringComparison.OrdinalIgnoreCase)) return i; return 99; }
    static readonly Dictionary<string, List<MeshRef>> listCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The model a character package's costume shows: the SkeletalMesh of its player class default's initialskeletalmesh
    /// component (default__marvelplayer_….initialskeletalmesh), or null. A moved costume points it at the copied model.
    /// </summary>
    public static string? CostumeMesh(string path)
    {
        try
        {
            var p = Package.Open(path);
            for (int i = 0; i < p.Exports.Length; i++)
            {
                var e = p.Exports[i];
                if (!e.ObjectName.Equals("initialskeletalmesh", StringComparison.OrdinalIgnoreCase)) continue;
                string at = p.PathOf(e);
                if (!at.StartsWith("marvelgamecontent.default__marvelplayer", StringComparison.OrdinalIgnoreCase)) continue;
                var d = p.ReadExportBytes(e);
                if (TagWalker.Walk(p, d, 16) is { } tags && tags.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) is { Size: 4 } sm
                    && BitConverter.ToInt32(d, sm.ValueAt) is int r && r > 0 && r <= p.Exports.Length)
                    return p.Exports[r - 1].ObjectName;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException or IndexOutOfRangeException) { }
        return null;
    }

    /// <summary>The skeletal meshes in a set of packages (file name, path): character packages only, costumes first; in each
    /// package the costume's own model (CostumeMesh) first.</summary>
    public static List<MeshRef> List(IEnumerable<(string File, string Path)> packages, bool anyPackage = false)
    {
        var result = new List<MeshRef>();
        foreach (var (file, path) in packages.Where(p => (anyPackage || Rank(p.File) < 99) && File.Exists(p.Path)).OrderBy(p => Rank(p.File)).ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase))
        {
            string key;
            try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks; } catch (IOException) { continue; }
            List<MeshRef>? found;
            lock (listCache)
                if (!listCache.TryGetValue(key, out found))
                {
                    found = [];
                    try
                    {
                        var pkg = AnimPackage.Open(path);
                        foreach (int i in pkg.FindExportsOfClass(SkeletalMeshReader.ClassName)) found.Add(new MeshRef(file, path, pkg.GetExportName(i), i));
                        // The costume's own model first (Kurt: a costume moved to another hero opened on the target's original
                        // model, which its package still holds; a hero's main package has props before the hero).
                        if (CostumeMesh(path) is string main && found.FindIndex(m => m.Name.Equals(main, StringComparison.OrdinalIgnoreCase)) is int at and > 0)
                        {
                            var first = found[at]; found.RemoveAt(at); found.Insert(0, first);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidPackageException or ArgumentException or IndexOutOfRangeException) { }
                    listCache[key] = found;
                }
            result.AddRange(found);
        }
        return result;
    }

    public static List<MeshRef> List(Mod m) => List(m.Manifest.UpkReplacements.Select(f => (f, Path.Combine(m.Folder, f))));

    public sealed record Loaded(string Name, Vector3[] Positions, Vector3[] Normals, Vector4[] Tangents, Vector2[] Uv, int[] Indices, int[] TriangleSection,
        Gui.ModelView.Look?[] Looks, string Info, IReadOnlyList<MeshBone> Bones, IReadOnlyList<VertexInfluence> Influences)
    {
        /// <summary>The mesh's RotOrigin then Origin (native data; UE3 draws the mesh turned and moved by them): Blade's
        /// motorcycle turns 90° (yaw 16384), Ghost Rider's chains too. Identity for nearly every mesh (Kurt, 2026-10-03).</summary>
        public System.Numerics.Matrix4x4 MeshTransform { get; init; } = System.Numerics.Matrix4x4.Identity;
    }

    /// <summary>A UE3 rotator (pitch, yaw, roll; 65536 = a full turn) as a matrix (UE3's FRotationMatrix, Z up).</summary>
    public static System.Numerics.Matrix4x4 Rotator(int pitch, int yaw, int roll) => Fx.PowerEffects.Player.UeRotation(
        pitch * MathF.PI * 2 / 65536, yaw * MathF.PI * 2 / 65536, roll * MathF.PI * 2 / 65536);

    /// <summary>Reads a mesh (highest detail, bind pose) and its section textures; null with a reason when it can't be read.</summary>
    /// <summary>A material (an instance) whose parent chain is a hologram material (name containing "hologram").</summary>
    static bool IsHologram(Package mpm, int mat)
    {
        for (int r = mat, depth = 0; r != 0 && depth < 6; depth++)
        {
            string name = r > 0 ? mpm.Exports[r - 1].ObjectName : mpm.RefName(r);
            if (name.Contains("hologram", StringComparison.OrdinalIgnoreCase)) return true;
            if (r < 0) return false;
            byte[] d = mpm.ReadExportBytes(mpm.Exports[r - 1]).ToArray();
            var t = TagWalker.Walk(mpm, d, 4)?.FirstOrDefault(x => x.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase) && x.Size == 4);
            if (t == null) return false;
            r = BitConverter.ToInt32(d, t.ValueAt);
        }
        return false;
    }

    /// <summary>The Materials list of the package's character mesh component (marvelgamecontent.default__&lt;class&gt;
    /// .initialskeletalmesh) when that component shows this mesh: object references by section material slot (0 = the mesh's
    /// own); null when there's none.</summary>
    public static List<int>? ComponentMaterials(Package mpm, string file, string meshName)
    {
        try
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            if (stem.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) stem = stem[4..];
            if (stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
            int comp = Array.FindIndex(mpm.Exports, e => mpm.PathOf(e).Equals($"marvelgamecontent.default__{stem.ToLowerInvariant()}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
            if (comp < 0) return null;
            byte[] d = mpm.ReadExportBytes(mpm.Exports[comp]).ToArray();
            var tags = TagWalker.Walk(mpm, d, 16);
            if (tags == null) return null;
            // only when the component shows this mesh (its SkeletalMesh is this export, or it names none: the class's own)
            if (tags.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } sm
                && BitConverter.ToInt32(d, sm.ValueAt) is int mr && mr > 0 && !mpm.Exports[mr - 1].ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase)) return null;
            if (tags.FirstOrDefault(t => t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase)) is not { } mt) return null;
            int n = BitConverter.ToInt32(d, mt.ValueAt);
            if (n <= 0 || mt.Size != 4 + 4 * n) return null;
            return Enumerable.Range(0, n).Select(k => BitConverter.ToInt32(d, mt.ValueAt + 4 + 4 * k)).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    /// <param name="componentMaterials">false: the mesh's own materials even where its component swaps them (the Powers tab
    /// previewing a hologram taken off).</param>
    public static Loaded? Load(MeshRef r, string? cacheFolder, out string why, bool componentMaterials = true)
    {
        string failure = "";
        var pkg = AnimPackage.Open(r.File);
        var mesh = SkeletalMeshReader.TryRead(pkg, r.Export, e => failure = e);
        why = failure;
        if (mesh?.HighestDetail is not { HasGeometry: true } lod) { why = failure.Length > 0 ? failure : "no geometry"; return null; }
        // The mesh's materials (section MaterialIndex → object reference): not a property but the native data's first
        // array, right after the bounds (AnimExportCli's reader skips it there: "SkipObjectArray ... materials").
        var materials = MaterialRefs(pkg, r.Export);
        var looks = new Gui.ModelView.Look?[lod.Sections.Count];
        var notes = new List<string>();
        Package? mpm = null;
        try { mpm = Package.Open(r.File); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { notes.Add("textures: " + ex.Message); }
        // The character's mesh component can swap the mesh's materials for its own (its Materials list: the Holo Wolverine
        // team-up's hologram): the game draws those, so the preview does too (Kurt, 2026-10-05).
        var ownMaterials = materials;
        if (componentMaterials && mpm != null && ComponentMaterials(mpm, r.File, r.Name) is { Count: > 0 } over)
        {
            materials = [.. materials];
            for (int k = 0; k < over.Count && k < materials.Count; k++) if (over[k] != 0) materials[k] = over[k];
            notes.Add($"materials from the mesh component: {over.Count(x => x != 0)}");
        }
        var cache = new Dictionary<int, Gui.ModelView.Map?>();
        var otherCaches = new Dictionary<Package, Dictionary<int, Gui.ModelView.Map?>>();
        for (int s = 0; s < lod.Sections.Count && mpm != null; s++)
        {
            int mi = lod.Sections[s].MaterialIndex;
            int mat = mi >= 0 && mi < materials.Count ? materials[mi] : 0;
            if (mat == 0) { notes.Add($"section {s}: no material"); continue; }
            if (mat < 0 && ImportedMaterial(r, mpm, mat, cacheFolder) is var (opkg, oexp))
            {
                // Imported (Savage She-Hulk's hair lives in UC__MarvelPlayer_SheHulk_SF): read it where it lives.
                if (!otherCaches.TryGetValue(opkg, out var oc)) otherCaches[opkg] = oc = [];
                looks[s] = LookFor(opkg, oexp + 1, cacheFolder, oc, notes, s);
                continue;
            }
            looks[s] = LookFor(mpm, mat, cacheFolder, cache, notes, s);
            // A hologram (the Holo Wolverine team-up's override, parent chvfxmaterial_chbasemat_hologram): the preview can't run
            // its shader, so it shows the mesh's own textures as a cyan glow, darkened, to tell it apart from the plain look.
            if (mat > 0 && IsHologram(mpm, mat) && mi < ownMaterials.Count && ownMaterials[mi] > 0 && LookFor(mpm, ownMaterials[mi], cacheFolder, cache, notes, s) is { } plain)
            {
                var cyan = new Vector3(0.25f, 0.85f, 1f);
                var glow = plain.Diffuse?.Recolored(c => cyan * (0.35f + 0.9f * (0.299f * c.X + 0.587f * c.Y + 0.114f * c.Z)));
                plain.Diffuse = plain.Diffuse?.Recolored(c => c * 0.15f);
                plain.EmissiveTex = glow; plain.UseEmissive = glow != null; plain.EmissiveMult = 1.2f;
                plain.UseSpec = false; plain.UseReflection = false;
                looks[s] = plain;
                notes.Add($"section {s}: hologram (shown as a cyan glow)");
            }
        }
        var tri = new int[lod.Indices.Count / 3];
        for (int s = 0; s < lod.Sections.Count; s++)
        {
            var sec = lod.Sections[s];
            for (int k = 0; k < sec.TriangleCount; k++) if (sec.BaseIndex / 3 + k < tri.Length) tri[sec.BaseIndex / 3 + k] = s;
        }
        int shown = looks.Count(t => t?.Diffuse != null);
        Vector3[] positions = [.. lod.Positions], normals = [.. lod.Normals];
        Vector2[] uvs = [.. lod.TexCoords];
        int[] indices = [.. lod.Indices];
        return new Loaded(r.Name, positions, normals, Tangents(positions, normals, uvs, indices), uvs, indices, tri, looks,
            $"{lod.Positions.Count:N0} vertices, {lod.TriangleCount:N0} triangles, textures {shown} of {looks.Length}" + (notes.Count > 0 ? "; " + string.Join("; ", notes.Distinct().Take(3)) : ""),
            mesh.Bones, lod.Influences)
        {
            MeshTransform = mesh.RotOrigin == (0, 0, 0) && mesh.Origin == Vector3.Zero ? System.Numerics.Matrix4x4.Identity
                : Rotator(mesh.RotOrigin.Pitch, mesh.RotOrigin.Yaw, mesh.RotOrigin.Roll) * System.Numerics.Matrix4x4.CreateTranslation(mesh.Origin),
        };
    }

    /// <summary>
    /// Per-vertex tangents from the UVs (the standard UV-gradient tangent, Lengyel's method): xyz along +U, w the sign that
    /// turns cross(normal, tangent) into the direction of +V, the bitangent of a DirectX-style (UE3) normal map.
    /// </summary>
    public static Vector4[] Tangents(Vector3[] p, Vector3[] n, Vector2[] uv, int[] idx)
    {
        var t1 = new Vector3[p.Length]; var t2 = new Vector3[p.Length];
        for (int k = 0; k + 2 < idx.Length; k += 3)
        {
            int a = idx[k], b = idx[k + 1], c = idx[k + 2];
            if (a >= uv.Length || b >= uv.Length || c >= uv.Length) continue;
            Vector3 e1 = p[b] - p[a], e2 = p[c] - p[a];
            Vector2 d1 = uv[b] - uv[a], d2 = uv[c] - uv[a];
            float det = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(det) < 1e-12f) continue;
            float r = 1f / det;
            var sdir = (e1 * d2.Y - e2 * d1.Y) * r;
            var tdir = (e2 * d1.X - e1 * d2.X) * r;
            t1[a] += sdir; t1[b] += sdir; t1[c] += sdir;
            t2[a] += tdir; t2[b] += tdir; t2[c] += tdir;
        }
        var result = new Vector4[p.Length];
        for (int i = 0; i < p.Length; i++)
        {
            var nn = i < n.Length ? n[i] : Vector3.UnitZ;
            var t = t1[i] - nn * Vector3.Dot(nn, t1[i]);
            if (t.LengthSquared() < 1e-12f) t = MathF.Abs(nn.X) < 0.9f ? Vector3.Cross(nn, Vector3.UnitX) : Vector3.Cross(nn, Vector3.UnitY);
            t = Vector3.Normalize(t);
            float w = Vector3.Dot(Vector3.Cross(nn, t), t2[i]) < 0 ? -1f : 1f;
            result[i] = new Vector4(t, w);
        }
        return result;
    }

    // Which channel of a packed map holds what: the words of its parameter name in order, R G B A
    // ("specmult_specpow_reflectivity_emissive", "specmultrimmaskreflection", "specmult_specpow_skinmask_reflectivity").
    static readonly string[] PackedWords = ["specmult", "specpow", "rimmask", "reflect", "emissive", "skinmask", "diffuse"];
    static int[] PackedChannels(string param)
    {
        var at = PackedWords.Select(w => (w, i: param.IndexOf(w, StringComparison.OrdinalIgnoreCase))).Where(x => x.i >= 0).OrderBy(x => x.i).Select(x => x.w).ToList();
        return [.. PackedWords.Select(w => at.IndexOf(w) is int k && k < 4 ? k : -1)];
    }
    static bool IsPacked(string param) => PackedWords.Count(w => param.Contains(w, StringComparison.OrdinalIgnoreCase)) >= 2;

    /// <summary>How a section is shaded, from its material (ModMaterials); a plain colour texture when it can't be read.</summary>
    static Gui.ModelView.Look? LookFor(Package pkg, int mat, string? cacheFolder, Dictionary<int, Gui.ModelView.Map?> cache, List<string> notes, int s)
    {
        MaterialInfo? mi = null;
        try { mi = ModMaterials.Read(pkg, mat); } catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        Gui.ModelView.Map? Map(int export) => export < 0 ? null : LoadMap(pkg, export, cacheFolder, cache, notes, s);
        if (mi is { Ghost: true })
        {
            // An effect material: its colour parameter (a name with "color", else the first), glowing when additive.
            var col = mi.Vectors.FirstOrDefault(v => v.Key.Contains("color", StringComparison.OrdinalIgnoreCase)).Value is var cv && cv != default ? cv
                : mi.Vectors.Values.FirstOrDefault(new System.Numerics.Vector4(0.6f, 0.75f, 1f, 1));
            return new Gui.ModelView.Look { Ghost = true, GhostAdditive = mi.BlendMode.Contains("additive", StringComparison.OrdinalIgnoreCase), GhostColor = new System.Numerics.Vector3(col.X, col.Y, col.Z) };
        }
        int diffuseAt = -1;
        if (mi != null)
            foreach (var (k, v) in mi.Textures)
                if (k.Contains("diffuse", StringComparison.OrdinalIgnoreCase) && !IsPacked(k)) { diffuseAt = v; break; }
        if (mi == null || diffuseAt < 0)
        {
            // Not a character material we can read: the colour texture as MPM's Meshes tab picks it, lit plainly.
            int pick = SectionTexture(pkg, mat, notes, s);
            return pick < 0 ? null : Gui.ModelView.Look.Plain(Map(pick));
        }
        var look = new Gui.ModelView.Look
        {
            Diffuse = Map(diffuseAt),
            UseNormal = mi.Switch("usenormalmap", true),
            UseSpec = mi.Switch("usespec") || mi.Switch("usespecular"),
            UseRim = mi.Switch("userimlight", true),
            RimMask = mi.Switch("userimmask"),
            DiffuseInRim = mi.Switch("usediffuseinrim"),
            HalfLambert = mi.Switch("usehalflambert"),
            Fill = mi.Switch("use_filllight"),
            UseEmissive = mi.Switch("useemissive") || mi.Switch("use_emissivergb"),
            Cutout = mi.Masked || mi.Parent.Contains("hair", StringComparison.OrdinalIgnoreCase) || mi.Parent.Contains("doublesided", StringComparison.OrdinalIgnoreCase)
                     || mi.Switches.Keys.Any(k => k.StartsWith("opacitymask", StringComparison.OrdinalIgnoreCase)),
            TwoSided = mi.Parent.Contains("doublesided", StringComparison.OrdinalIgnoreCase) || mi.Parent.Contains("hair", StringComparison.OrdinalIgnoreCase),
            NormalStrength = mi.Scalar("normalstrength", 1),
            SpecStrength = 1, SpecPower = 16,
            EmissiveMult = mi.Scalar("emissivemultiplier", 1),
            Ambient = Math.Clamp(mi.Scalar("ambientmult", 1), 0, 3),
        };
        // Specular numbers by material generation (see ModelView.Look; values from a survey of 96 stock materials).
        if (mi.Parent.Contains("_v2", StringComparison.OrdinalIgnoreCase))
        {
            look.SpecStrength = Math.Clamp(mi.Scalar("specmult", 1), 0, 6);
            float sp = mi.Scalar("specularpower", 0);
            if (mi.Switch("usespecpowermask")) { look.SpecPower = sp > 0 ? sp : 1; look.SpecPowerMask = mi.Scalar("specularpowermask", 255); }
            else look.SpecPower = sp > 1 ? sp : 16;
            if (mi.Switch("usediffusemultspec")) { look.DiffuseSpec = true; look.DiffuseSpecMult = mi.Scalar("diffusespecmult", 1); look.SpecDesat = mi.Scalar("speccolordesat", 0); }
        }
        else
        {
            float total = mi.Scalar("totalspecmult", 1);
            look.SpecStrength = Math.Clamp(mi.Scalar("specmult1", 1) * total, 0, 6);
            float lo = mi.Scalar("specularpower1min", 0), hi = mi.Scalar("specularpower1max", 0);
            look.SpecPower = lo > 0 ? lo : hi > 0 ? hi : 16;
            if (hi > look.SpecPower) look.SpecPowerMax = hi;
            if (mi.Scalar("specmult2", 0) is float s2 && s2 > 0)
            {
                look.Spec2Strength = Math.Clamp(s2 * total, 0, 6);
                float lo2 = mi.Scalar("specularpower2min", 0), hi2 = mi.Scalar("specularpower2max", 0);
                look.Spec2Min = lo2 > 0 ? lo2 : hi2 > 0 ? hi2 : 16; look.Spec2Max = Math.Max(look.Spec2Min, hi2);
            }
        }
        if (mi.Vectors.TryGetValue("speccolorvalue", out var scv)) look.SpecTint = new Vector3(scv.X, scv.Y, scv.Z);
        int normalAt = mi.Texture("normaltex", "hairnorm", "norm");
        if (normalAt >= 0) look.Normal = Map(normalAt);
        else look.UseNormal = false;
        // Every packed map, by the words of its name (the first map naming a value wins): chbasematerial (v1) has
        // specmultrimmaskreflection and emissivespecpow (Angel: R 0, G detail, B 0, so R glow mask, G spec power; stock
        // Gambit Death: glow only on the emblem); v2 has specmult_specpow_reflectivity_emissive or …_skinmask_….
        foreach (var (k, v) in mi.Textures)
            if (IsPacked(k) && Map(v) is { } pm)
            {
                var ch = PackedChannels(k);
                if (look.Spec.Map == null && ch[0] >= 0) look.Spec = new(pm, ch[0]);
                if (look.SpecPow.Map == null && ch[1] >= 0) look.SpecPow = new(pm, ch[1]);
                if (look.RimMaskAt.Map == null && ch[2] >= 0) look.RimMaskAt = new(pm, ch[2]);
                // (A glow channel in an alpha that never varies is no mask: a DXT1 map has none and reads 255 everywhere,
                // which lit Rescue's whole model, Kurt 2026-10-02.)
                if (look.Emissive.Map == null && ch[4] >= 0 && !(ch[4] == 3 && !pm.AlphaVaries)) look.Emissive = new(pm, ch[4]);
                if (look.ReflectAt.Map == null && ch[3] >= 0) look.ReflectAt = new(pm, ch[3]);
            }
        // Reflections and a separate glow texture (see ModelView.Look).
        look.UseReflection = mi.Switch("usereflection") || mi.Switch("alwaysusereflection");
        int rt = mi.Texture("reflectiontex");
        if (look.UseReflection && rt >= 0) look.Reflection = Map(rt);
        look.ReflectMult = Math.Clamp(mi.Scalar("reflectionmult", 1), 0, 4);
        look.FresnelPower = mi.Scalar("fresnelpower", 0);
        look.ReflectByDiffuse = mi.Switch("multiplyreflectionbydiffuse");
        foreach (var (k, v) in mi.Textures)
            if (k.Contains("emissive", StringComparison.OrdinalIgnoreCase) && !IsPacked(k)) { look.EmissiveTex = Map(v); if (mi.Switch("use_emissivergb") || mi.Switch("useemissive")) look.UseEmissive = true; break; }
        // A full-colour glow texture is the glow (use_emissivergb); the packed map's glow channel isn't added on top.
        if (look.EmissiveTex != null) look.Emissive = default;
        int sc = mi.Texture("speccolortex");
        if (sc >= 0) look.SpecColor = Map(sc);
        if (mi.Switch("useemissivespecpow")) look.UseEmissive = true;
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_LOOKDEBUG") == "1")
            Console.WriteLine($"look s{s} {mi.Parent}: refl={look.UseReflection} map={look.Reflection != null} mask={(look.ReflectAt.Map != null)} mult={look.ReflectMult} fres={look.FresnelPower} bydiff={look.ReflectByDiffuse} spec={look.UseSpec} str={look.SpecStrength} pow={look.SpecPower}/{look.SpecPowerMax}/{look.SpecPowerMask} emTex={look.EmissiveTex != null} em={look.UseEmissive}");
        if (mi.Vectors.TryGetValue("rimcolor", out var rc)) look.Rim = new Vector3(rc.X, rc.Y, rc.Z) * mi.Scalar("rimcolormult", 1);
        if (mi.Vectors.TryGetValue("filllightcolor", out var fc)) look.FillColor = new Vector3(fc.X, fc.Y, fc.Z) * Math.Clamp(mi.Scalar("filllightamount", 5) / 5f, 0, 2);
        return look;
    }

    static Gui.ModelView.Map? LoadMap(Package pkg, int export, string? cacheFolder, Dictionary<int, Gui.ModelView.Map?> cache, List<string> notes, int s)
    {
        if (cache.TryGetValue(export, out var m)) return m;
        m = null;
        try
        {
            if (TextureExport.ReadBestMip(pkg, export, out string note, cacheFolder) is { } mip && TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is byte[] px)
                m = new Gui.ModelView.Map(px, mip.Width, mip.Height);
            else notes.Add($"section {s}: {pkg.Exports[export].ObjectName} can't be shown ({note})");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException) { notes.Add($"section {s}: {pkg.Exports[export].ObjectName}: {ex.Message}"); }
        cache[export] = m;
        return m;
    }

    /// <summary>A held prop as the game defines it: a mesh and the character bone it's attached to (weapon slot for info).</summary>
    public sealed record Attachment(string Mesh, string? Bone, string? Slot)
    {
        /// <summary>VisibilityPoint = visible_on_demand: shown only while a power shows it (Punisher's shotgun, RPG …);
        /// otherwise always held (his sidearms).</summary>
        public bool OnDemand { get; init; }
        /// <summary>The attachment's class (marvelattachment_punisher_shotgun), as a costume's mAttachmentClasses names it.</summary>
        public string Class { get; init; } = "";
        /// <summary>Every WeaponSlot it fills (a power's SwitchAttachments turns slots off and on: bothhands → pumpshotgun).</summary>
        public IReadOnlyList<string> Slots { get; init; } = [];
        /// <summary>bAttachUseParentAnim: rigged to the character's skeleton and moved by the character's animation (Taskmaster's
        /// bow, Punisher's flamethrower): no bone of its own.</summary>
        public bool UseParentAnim { get; init; }
        /// <summary>OffsetRotation (pitch, yaw, roll): the prop turned on its bone. Every vehicle that sets one turns 90°
        /// (Hawkeye's and Kate Bishop's Sky-Cycle, Ghost Rider's bikes, Doom's throne, Lockheed …; Kurt 2026-10-03).</summary>
        public (int Pitch, int Yaw, int Roll) OffsetRotation { get; init; }
    }

    /// <summary>
    /// The props a package attaches (Kurt: characters holding a sword or hammer): its "marvelattachment_…" objects name
    /// the prop mesh (ModelMesh) and the bone it's held on (AttachmentBones), e.g. Thor's hammer: thorhammer → g_r_palm,
    /// slot righthand. A costume's own attachment (thorhammer_ageofultron) only swaps the mesh and inherits the bone from
    /// the one its name extends, so the bone comes from the longest such name that has one.
    /// </summary>
    public static List<Attachment> Attachments(string packagePath, string? parentPackage = null)
    {
        // Class defaults leave out what equals their parent class's, so bone, slots and visibility come from the longest
        // class name this one extends that sets them (here, or in parentPackage: a costume's attachment extending the hero's).
        var raw = RawAttachments(packagePath);
        var parents = parentPackage != null ? [.. raw, .. RawAttachments(parentPackage)] : raw;
        // The class it extends: the Class export's SuperStruct when read (Punisher's sidearmleft extends marvelattachment,
        // not marvelattachment_punisher, which its name starts with: going by names made the pistols on-demand like the
        // rifle), else the longest class name it starts with.
        RawAttachment? Parent(RawAttachment a)
        {
            if (a.Super != null) return parents.FirstOrDefault(p => p.Class.Equals(a.Super, StringComparison.OrdinalIgnoreCase));
            return parents.Where(p => a.Class.StartsWith(p.Class, StringComparison.OrdinalIgnoreCase) && p.Class.Length < a.Class.Length).OrderByDescending(p => p.Class.Length).FirstOrDefault();
        }
        T? Inherited<T>(RawAttachment a, Func<RawAttachment, T?> get)
        {
            for (RawAttachment? c = a; c != null; c = Parent(c))
            {
                if (get(c) is { } v) return v;
                if (c != a && ReferenceEquals(c, Parent(c))) break;
            }
            return default;
        }
        T? From<T>(RawAttachment a, Func<RawAttachment, T?> get) where T : class => Inherited(a, get);
        bool OnDemandOf(RawAttachment a) { for (RawAttachment? c = a; c != null; c = Parent(c)) if (c.OnDemand is bool v) return v; return false; }
        bool ParentAnimOf(RawAttachment a) { for (RawAttachment? c = a; c != null; c = Parent(c)) if (c.UseParentAnim is bool v) return v; return false; }
        var result = new List<Attachment>();
        foreach (var a in raw)
        {
            var slots = From(a, x => x.Slots) ?? [];
            // (the mesh too: a default that sets only its slots has its parent class's)
            var meshes = From(a, x => x.Meshes.Count > 0 ? x.Meshes : null) ?? (a.GuessMesh != null ? [a.GuessMesh] : []);
            foreach (string m in meshes)
                result.Add(new Attachment(m, From(a, x => x.Bone), slots.FirstOrDefault()) { OnDemand = OnDemandOf(a), Class = a.Class, Slots = slots, UseParentAnim = ParentAnimOf(a), OffsetRotation = Inherited(a, x => x.OffsetRotation) ?? (0, 0, 0) });
        }
        return result;
    }

    sealed record RawAttachment(string Class, List<string> Meshes, string? Bone, List<string>? Slots, bool? OnDemand, string? Super)
    {
        public bool? UseParentAnim { get; init; }
        public (int Pitch, int Yaw, int Roll)? OffsetRotation { get; init; }
        /// <summary>A skeletal mesh of the package whose name ends the class name (marvelattachment_kittypryde_katana →
        /// katana): used only when the class sets no mesh and none is inherited (a guess: Kitty's katana names none, its
        /// parent class isn't in the package, and the package has a mesh "katana").</summary>
        public string? GuessMesh { get; init; }
    }

    static readonly Dictionary<string, List<RawAttachment>> rawCache = new(StringComparer.OrdinalIgnoreCase);

    static List<RawAttachment> RawAttachments(string packagePath)
    {
        string key;
        try { key = packagePath + "|" + File.GetLastWriteTimeUtc(packagePath).Ticks; } catch (IOException) { return []; }
        lock (rawCache) if (rawCache.TryGetValue(key, out var hit)) return hit;
        var raw = new List<RawAttachment>();
        try
        {
            var pkg = Package.Open(packagePath);
            var skeletal = pkg.Exports.Where(x => pkg.ClassOf(x).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)).Select(x => x.ObjectName).ToList();
            for (int i = 0; i < pkg.Exports.Length; i++)
            try
            {
                // (each export on its own: one that doesn't read stopped the rest, and Kitty's katana and Black Cat's
                // whips, defaults in their base packages, went missing)
                var e = pkg.Exports[i];
                string cls = pkg.ClassOf(e);
                if (!cls.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.StartsWith("default__", StringComparison.OrdinalIgnoreCase)) continue;
                var d = pkg.ReadExportBytes(e);
                if (TagWalker.Walk(pkg, d, 4) is not { } tags) continue;
                var meshes = new List<string>(); string? bone = null; List<string>? slots = null; bool? onDemand = null, parentAnim = null; (int, int, int)? offsetRot = null;
                foreach (var t in tags)
                {
                    int count = t.Size >= 4 ? BitConverter.ToInt32(d, t.ValueAt) : 0;
                    if (t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase))
                        for (int k = 0; k < count && t.ValueAt + 8 + 4 * k <= t.End; k++) { int r = BitConverter.ToInt32(d, t.ValueAt + 4 + 4 * k); if (r != 0) meshes.Add(r > 0 ? pkg.Exports[r - 1].ObjectName : pkg.RefName(r)); }
                    else if (t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4 && BitConverter.ToInt32(d, t.ValueAt) is int cr && cr > 0 && cr <= pkg.Exports.Length)
                    {
                        // An animated attachment (MarvelAttachmentAnimated: Black Cat's whips, bikes, chains) names a
                        // skeletal mesh component; its SkeletalMesh is the prop (component properties from byte 16).
                        var cd = pkg.ReadExportBytes(pkg.Exports[cr - 1]);
                        if (TagWalker.Walk(pkg, cd, 16) is { } ct && ct.FirstOrDefault(x => x.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) is { Size: 4 } sm
                            && BitConverter.ToInt32(cd, sm.ValueAt) is int mr && mr != 0)
                            meshes.Add(mr > 0 ? pkg.Exports[mr - 1].ObjectName : pkg.RefName(mr));
                    }
                    else if (t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase) && count > 0) bone = TagWalker.NameAt(pkg, d, t.ValueAt + 4);
                    else if (t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase))
                    {
                        slots = [];
                        for (int k = 0; k < count && t.ValueAt + 4 + 8 * (k + 1) <= t.End; k++) slots.Add(TagWalker.NameAt(pkg, d, t.ValueAt + 4 + 8 * k));
                    }
                    else if (t.Name.Equals("bAttachUseParentAnim", StringComparison.OrdinalIgnoreCase)) parentAnim = d[t.ValueAt - 1] != 0;
                    else if (t.Name.Equals("OffsetRotation", StringComparison.OrdinalIgnoreCase) && t.Size == 12)
                        offsetRot = (BitConverter.ToInt32(d, t.ValueAt), BitConverter.ToInt32(d, t.ValueAt + 4), BitConverter.ToInt32(d, t.ValueAt + 8));
                    else if (t.Name.Equals("VisibilityPoint", StringComparison.OrdinalIgnoreCase))
                        onDemand = TagWalker.NameAt(pkg, d, t.ValueAt).Contains("on_demand", StringComparison.OrdinalIgnoreCase);
                }
                raw.Add(new RawAttachment(cls, meshes, bone, slots, onDemand, SuperOf(pkg, cls))
                {
                    UseParentAnim = parentAnim,
                    OffsetRotation = offsetRot,
                    GuessMesh = skeletal.Where(m => cls.EndsWith("_" + m, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Length).FirstOrDefault(),
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        lock (rawCache) rawCache[key] = raw;
        return raw;
    }

    /// <summary>The class a class export extends (its SuperStruct: after the object's NetIndex and empty property list come
    /// UField.Next and then SuperStruct), or null when the class isn't exported here or doesn't read as expected.</summary>
    static string? SuperOf(Package pkg, string className)
    {
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!e.ObjectName.Equals(className, StringComparison.OrdinalIgnoreCase) || !pkg.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var d = pkg.ReadExportBytes(e);
                if (TagWalker.Walk(pkg, d, 4) is not { } tags || tags.NoneAt + 16 > d.Length) return null;
                int r = BitConverter.ToInt32(d, tags.NoneAt + 12);
                string name = r > 0 && r <= pkg.Exports.Length ? pkg.Exports[r - 1].ObjectName : r < 0 ? pkg.RefName(r) : "";
                return name.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase) || name.Equals("Actor", StringComparison.OrdinalIgnoreCase) ? name : null;
            }
            catch (Exception ex) when (ex is PackageFormatException or ArgumentException or IndexOutOfRangeException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// The attachment classes a character package's player class default lists (mAttachmentClasses: Punisher's 17 guns,
    /// Thor Age of Ultron's own hammer), by class name; null when the package has no player default or it doesn't set
    /// them (a costume class that keeps its hero's list).
    /// </summary>
    public static List<string>? AttachmentClasses(string packagePath)
    {
        try
        {
            var pkg = Package.Open(packagePath);
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                var e = pkg.Exports[i];
                if (!e.ObjectName.StartsWith("default__marvelplayer", StringComparison.OrdinalIgnoreCase) || !pkg.ClassOf(e).StartsWith("marvelplayer", StringComparison.OrdinalIgnoreCase)) continue;
                var d = pkg.ReadExportBytes(e);
                if (TagWalker.Walk(pkg, d, 4) is not { } tags) continue;
                foreach (var t in tags.Where(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase)))
                {
                    int count = BitConverter.ToInt32(d, t.ValueAt);
                    var list = new List<string>();
                    for (int k = 0; k < count && t.ValueAt + 8 + 4 * k <= t.End; k++) { int r = BitConverter.ToInt32(d, t.ValueAt + 4 + 4 * k); if (r != 0) list.Add(r > 0 ? pkg.Exports[r - 1].ObjectName : pkg.RefName(r)); }
                    return list;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        return null;
    }

    /// <summary>
    /// One thing a power does to a character's props (from a PowerFxMeshAttachment component of its package; the
    /// --attach-census of all 5,559 power and hero packages lists every property these use). Show or hide a weapon slot or
    /// (Target "class:&lt;name&gt;") an attachment class, from Start until End (End null = to the animation's end). Start and
    /// End are a point (power_on_start = 0, a contact / target-result point = the power's contact time, power_on_end = the
    /// animation's end) plus seconds.
    /// </summary>
    public sealed record PropRule(bool Show, string Target, string StartPoint, float StartOffset, string? EndPoint, float EndOffset);

    static readonly Dictionary<string, List<PropRule>> ruleCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The prop rules of a power package's PowerFxMeshAttachment components:
    /// WeaponSlotToShowOnActivate / ToHideOnActivate (a slot from the activation on), WhileActivatedShowWeaponSlot /
    /// HideWeaponSlot and WhileActivatedAttachmentClass (until the deactivation point), AttachmentsToShowOnActivate /
    /// ToHideOnActivate (classes), SwitchAttachments { From, To } (Punisher's Buckshot Blast: bothhands → pumpshotgun).
    /// Components that need a condition or another entity (ConditionRequired, EntityRequired: a buff, a summon) are left
    /// out: the preview has neither.
    /// </summary>
    public static List<PropRule> PropRules(string powerPackage)
    {
        lock (ruleCache) if (ruleCache.TryGetValue(powerPackage, out var hit)) return hit;
        var result = new List<PropRule>();
        try
        {
            var pkg = Package.Open(powerPackage);
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                var e = pkg.Exports[i];
                // A condition's too: a ride's bike is shown by its condition (marvelconditioneffect_shared_ridemotorcycle:
                // show slot bike while active).
                if (!pkg.ClassOf(e).Equals("powerfxmeshattachment", StringComparison.OrdinalIgnoreCase) && !pkg.ClassOf(e).Equals("conditionfxmeshattachment", StringComparison.OrdinalIgnoreCase)) continue;
                var d = pkg.ReadExportBytes(e);
                if (TagWalker.Walk(pkg, d, 16) is not { } tags) continue;   // a component: properties from byte 16
                TagWalker.Tag? T(string n) => tags.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (T("ConditionRequired") != null || T("EntityRequired") != null) continue;
                string? Nm(string n) => T(n) is { } t ? TagWalker.NameAt(pkg, d, t.ValueAt) : null;
                float Fl(string n) => T(n) is { } t && t.Size == 4 ? BitConverter.ToSingle(d, t.ValueAt) : 0;
                string start = Nm("ActivationPoint") ?? "power_on_start", end = Nm("DeactivationPoint") ?? "";
                float so = Fl("ActivationOffset"), eo = Fl("DeactivationOffset");
                string? endPoint = end.Length > 0 ? end : null;
                List<string> Classes(string n)
                {
                    var list = new List<string>();
                    if (T(n) is not { } t) return list;
                    if (t.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase)) { int r = BitConverter.ToInt32(d, t.ValueAt); if (r != 0) list.Add(r > 0 ? pkg.Exports[r - 1].ObjectName : pkg.RefName(r)); return list; }
                    int count = BitConverter.ToInt32(d, t.ValueAt);
                    for (int k = 0; k < count && t.ValueAt + 8 + 4 * k <= t.End; k++) { int r = BitConverter.ToInt32(d, t.ValueAt + 4 + 4 * k); if (r != 0) list.Add(r > 0 ? pkg.Exports[r - 1].ObjectName : pkg.RefName(r)); }
                    return list;
                }
                if (Nm("WeaponSlotToShowOnActivate") is { Length: > 0 } s1) result.Add(new PropRule(true, s1, start, so, null, 0));
                if (Nm("WeaponSlotToHideOnActivate") is { Length: > 0 } s2) result.Add(new PropRule(false, s2, start, so, null, 0));
                if (Nm("WhileActivatedShowWeaponSlot") is { Length: > 0 } s3) result.Add(new PropRule(true, s3, start, so, endPoint ?? "power_on_end", eo));
                if (Nm("WhileActivatedHideWeaponSlot") is { Length: > 0 } s4) result.Add(new PropRule(false, s4, start, so, endPoint ?? "power_on_end", eo));
                foreach (string c in Classes("WhileActivatedAttachmentClass")) result.Add(new PropRule(true, "class:" + c, start, so, endPoint ?? "power_on_end", eo));
                foreach (string c in Classes("AttachmentsToShowOnActivate")) result.Add(new PropRule(true, "class:" + c, start, so, null, 0));
                foreach (string c in Classes("AttachmentsToHideOnActivate")) result.Add(new PropRule(false, "class:" + c, start, so, null, 0));
                if (T("SwitchAttachments") is { } sw)
                {
                    void One(int at)
                    {
                        if (TagWalker.Walk(pkg, d, at) is not { } st) return;
                        string? from = st.FirstOrDefault(x => x.Name.Equals("From", StringComparison.OrdinalIgnoreCase)) is { } f ? TagWalker.NameAt(pkg, d, f.ValueAt) : null;
                        string? to = st.FirstOrDefault(x => x.Name.Equals("To", StringComparison.OrdinalIgnoreCase)) is { } o ? TagWalker.NameAt(pkg, d, o.ValueAt) : null;
                        if (from is { Length: > 0 }) result.Add(new PropRule(false, from, start, so, null, 0));
                        if (to is { Length: > 0 }) result.Add(new PropRule(true, to, start, so, null, 0));
                    }
                    if (sw.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase)) One(sw.ValueAt);
                    else if (sw.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
                    {
                        int count = BitConverter.ToInt32(d, sw.ValueAt), at = sw.ValueAt + 4;
                        for (int k = 0; k < count && at < sw.End; k++)
                        {
                            if (TagWalker.Walk(pkg, d, at) is not { } st) break;
                            One(at);
                            at = st.NoneAt + 8;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        lock (ruleCache) ruleCache[powerPackage] = result;
        return result;
    }

    /// <summary>For --material-probe: each section of a mesh (highest detail) with its material reference.</summary>
    public static List<(int Section, int Material)> SectionMaterials(MeshRef r)
    {
        var pkg = AnimPackage.Open(r.File);
        var mesh = SkeletalMeshReader.TryRead(pkg, r.Export, _ => { });
        if (mesh?.HighestDetail is not { } lod) return [];
        var materials = MaterialRefs(pkg, r.Export);
        return [.. lod.Sections.Select((s, i) => (i, s.MaterialIndex >= 0 && s.MaterialIndex < materials.Count ? materials[s.MaterialIndex] : 0))];
    }

    /// <summary>The mesh's materials: not a property but the native data's first array, right after the bounds.</summary>
    internal static List<int> MaterialRefs(AnimPackage pkg, int export)
    {
        var materials = new List<int>();
        if (pkg.TryReadProperties(export) is { } props)
        {
            var d = pkg.GetExportData(export);
            int at = props.PayloadOffset + MeshBounds.ByteSize;
            if (at + 4 <= d.Length)
            {
                int n = BitConverter.ToInt32(d.Slice(at, 4));
                for (int i = 0; i < n && i < 256 && at + 8 + 4 * i <= d.Length; i++) materials.Add(BitConverter.ToInt32(d.Slice(at + 4 + 4 * i, 4)));
            }
        }
        return materials;
    }

    /// <summary>
    /// A material a costume package imports (by name) from the hero's base package UC__MarvelPlayer_&lt;Hero&gt;_SF, as the
    /// game loads it: the copy next to the mesh's package (the mod's), else the game's. Null when it isn't found there.
    /// </summary>
    static (Package, int)? ImportedMaterial(MeshRef r, Package pkg, int mat, string? cooked) =>
        ImportedMaterialAt(r, pkg, mat, cooked) is { } at ? (at.Pkg, at.Index) : null;

    /// <summary><see cref="ImportedMaterial"/> with the file it was found in (its game file name and the path read). Also for
    /// a hero's audio / voice packages (UC__MarvelPlayerAudio_&lt;Hero&gt;_…: Jean Grey's Phoenix wings import their material
    /// from her base package, 2026-10-07).</summary>
    internal static (Package Pkg, int Index, string File, string Path)? ImportedMaterialAt(MeshRef r, Package pkg, int mat, string? cooked)
    {
        const string player = "UC__MarvelPlayer_", audio = "UC__MarvelPlayerAudio_";
        string? prefix = r.Package.StartsWith(audio, StringComparison.OrdinalIgnoreCase) ? audio : r.Package.StartsWith(player, StringComparison.OrdinalIgnoreCase) ? player : null;
        if (prefix == null) return null;
        string name;
        try { name = pkg.RefName(mat); } catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { return null; }
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        string hero = Path.GetFileNameWithoutExtension(r.Package)[prefix.Length..].Split('_')[0];
        string baseFile = $"{player}{hero}_SF.upk";
        if (baseFile.Equals(r.Package, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var path in new[] { Path.Combine(Path.GetDirectoryName(r.File) ?? "", baseFile), cooked == null ? "" : StockFiles.For(cooked, baseFile) })
        {
            if (path.Length == 0 || !File.Exists(path)) continue;
            try
            {
                var bp = Package.Open(path);
                for (int i = 0; i < bp.Exports.Length; i++)
                    if (bp.Exports[i].ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) && bp.ClassOf(bp.Exports[i]).Contains("Material", StringComparison.OrdinalIgnoreCase))
                        return (bp, i, baseFile, path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        }
        return null;
    }

    /// <summary>A section's colour texture (as MPM's Meshes tab chooses it).</summary>
    static int SectionTexture(Package pkg, int mat, List<string> notes, int s)
    {
        var list = TextureExport.MaterialTextures(pkg, mat, []);
        if (list.Count == 0 && mat > 0)
            try
            {
                var me = pkg.Exports[mat - 1];
                foreach (int r in ExportCopy.MaterialNativeTextures(pkg, pkg.ReadExportBytes(me), pkg.ClassOf(me)))
                    if (r > 0 && pkg.ClassOf(pkg.Exports[r - 1]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                        list.Add(new MaterialTexture("", pkg.Exports[r - 1].ObjectName, r - 1));
            }
            catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        static bool NotColour(string n) => n.Contains("norm", StringComparison.OrdinalIgnoreCase) || n.Contains("spec", StringComparison.OrdinalIgnoreCase)
            || n.Contains("mask", StringComparison.OrdinalIgnoreCase) || n.EndsWith("_n", StringComparison.OrdinalIgnoreCase) || n.Contains("cube", StringComparison.OrdinalIgnoreCase);
        var pick = list.FirstOrDefault(t => t.Parameter.Contains("diffuse", StringComparison.OrdinalIgnoreCase) || t.Parameter.Contains("basecolor", StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(t => t.Texture.Contains("diff", StringComparison.OrdinalIgnoreCase))
                ?? list.FirstOrDefault(t => !NotColour(t.Texture) && !NotColour(t.Parameter));
        if (pick == null) { notes.Add($"section {s}: no colour texture{(mat < 0 ? " (material from another package)" : "")}"); return -1; }
        return pick.ExportIndex;
    }
}
