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

    /// <summary>The skeletal meshes in a set of packages (file name, path): character packages only, costumes first.</summary>
    public static List<MeshRef> List(IEnumerable<(string File, string Path)> packages)
    {
        var result = new List<MeshRef>();
        foreach (var (file, path) in packages.Where(p => Rank(p.File) < 99 && File.Exists(p.Path)).OrderBy(p => Rank(p.File)).ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase))
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
        Gui.ModelView.Look?[] Looks, string Info, IReadOnlyList<MeshBone> Bones, IReadOnlyList<VertexInfluence> Influences);

    /// <summary>Reads a mesh (highest detail, bind pose) and its section textures; null with a reason when it can't be read.</summary>
    public static Loaded? Load(MeshRef r, string? cacheFolder, out string why)
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
            mesh.Bones, lod.Influences);
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
                if (look.Emissive.Map == null && ch[4] >= 0) look.Emissive = new(pm, ch[4]);
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
    public sealed record Attachment(string Mesh, string? Bone, string? Slot);

    /// <summary>
    /// The props a package attaches (Kurt: characters holding a sword or hammer): its "marvelattachment_…" objects name
    /// the prop mesh (ModelMesh) and the bone it's held on (AttachmentBones), e.g. Thor's hammer: thorhammer → g_r_palm,
    /// slot righthand. A costume's own attachment (thorhammer_ageofultron) only swaps the mesh and inherits the bone from
    /// the one its name extends, so the bone comes from the longest such name that has one.
    /// </summary>
    public static List<Attachment> Attachments(string packagePath)
    {
        var raw = new List<(string Class, List<string> Meshes, string? Bone, string? Slot)>();
        try
        {
            var pkg = Package.Open(packagePath);
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                var e = pkg.Exports[i];
                string cls = pkg.ClassOf(e);
                if (!cls.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.StartsWith("default__", StringComparison.OrdinalIgnoreCase)) continue;
                var d = pkg.ReadExportBytes(e);
                if (TagWalker.Walk(pkg, d, 4) is not { } tags) continue;
                var meshes = new List<string>(); string? bone = null, slot = null;
                foreach (var t in tags)
                {
                    int count = t.Size >= 4 ? BitConverter.ToInt32(d, t.ValueAt) : 0;
                    if (t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase))
                        for (int k = 0; k < count && t.ValueAt + 8 + 4 * k <= t.End; k++) { int r = BitConverter.ToInt32(d, t.ValueAt + 4 + 4 * k); if (r != 0) meshes.Add(r > 0 ? pkg.Exports[r - 1].ObjectName : pkg.RefName(r)); }
                    else if (t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase) && count > 0) bone = TagWalker.NameAt(pkg, d, t.ValueAt + 4);
                    else if (t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase) && count > 0) slot = TagWalker.NameAt(pkg, d, t.ValueAt + 4);
                }
                raw.Add((cls, meshes, bone, slot));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        var result = new List<Attachment>();
        foreach (var a in raw)
        {
            var parent = raw.Where(p => p.Bone != null && a.Class.StartsWith(p.Class, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Class.Length).FirstOrDefault();
            foreach (string m in a.Meshes) result.Add(new Attachment(m, a.Bone ?? parent.Bone, a.Slot ?? parent.Slot));
        }
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
    static (Package, int)? ImportedMaterial(MeshRef r, Package pkg, int mat, string? cooked)
    {
        const string player = "UC__MarvelPlayer_";
        if (!r.Package.StartsWith(player, StringComparison.OrdinalIgnoreCase)) return null;
        string name;
        try { name = pkg.RefName(mat); } catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException) { return null; }
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        string hero = Path.GetFileNameWithoutExtension(r.Package)[player.Length..].Split('_')[0];
        string baseFile = $"{player}{hero}_SF.upk";
        if (baseFile.Equals(r.Package, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var dir in new[] { Path.GetDirectoryName(r.File), cooked })
        {
            string path = dir == null ? "" : Path.Combine(dir, baseFile);
            if (dir == null || !File.Exists(path)) continue;
            try
            {
                var bp = Package.Open(path);
                for (int i = 0; i < bp.Exports.Length; i++)
                    if (bp.Exports[i].ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) && bp.ClassOf(bp.Exports[i]).Contains("Material", StringComparison.OrdinalIgnoreCase))
                        return (bp, i);
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
