using System.Globalization;
using System.Numerics;
using AnimPackage = AnimExportCli.Packages.Package;
using MpmPackage = MhoPackageModifier.Package;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The whole import of one MFF model onto one MHO base package, in steps: retarget → materials → mesh → package → Mod
/// Manager mod (folder + zip) → read-back checks → a check render. Everything written goes into the out folder; the base
/// package and the MFF source are only read. Progress and results go to <c>log</c> (the command line prints them).
/// </summary>
sealed class ImportBuild
{
    public sealed record Result(string Package, string ModFolder, string Zip, string ModName);

    readonly string mff, package, outDir;
    readonly ImportOptions o;
    readonly Action<string> log;

    MffModel? m;
    /// <summary>The source's name in the mod: the MFF folder, or the FBX's folder (its file name for a plain model.fbx's
    /// parent).</summary>
    string sourceName = "";
    MhoSkeleton sk = null!;
    Retargeted r = null!;
    AnimPackage pkg = null!;
    byte[] data = [];
    int payloadOffset;
    SkelNative baseN = null!;
    List<string> mats = [];
    List<EncSection> enc = [];
    /// <summary>Borrowed hair (Hair ▾): the rig whose bones were grafted onto <see cref="sk"/> before the retarget, and the
    /// retargeted skeleton (the built mesh's) its motion is made on.</summary>
    BorrowedRig? hairRig;
    /// <summary>Every borrowed rig grafted on (the cape first, then the hair, as the preview: 0.17.0).</summary>
    readonly List<BorrowedRig> rigs = [];
    List<AnimExportCli.Meshes.MeshBone> builtBones = [];
    int baseBones;
    float metalShare, glowShare;

    ImportBuild(string mff, string package, string outDir, ImportOptions options, Action<string> log)
    { this.mff = mff; this.package = package; this.outDir = outDir; o = options; this.log = log; }

    /// <summary>Runs the import; null when the package failed its check (the problems are logged).</summary>
    public static Result? Run(string mff, string package, string outDir, ImportOptions options, Action<string> log) =>
        new ImportBuild(mff, package, outDir, options, log).Run();

    Result? Run()
    {
        Protected.CheckWrite(outDir);
        Prepare();
        var (basePackage, matRefs) = Materials();
        var extraNames = HairNames(ref basePackage);
        var (export, native) = EncodeMesh(matRefs, extraNames);
        if (!PropFit.IsProp(sk)) basePackage = NoMorphs(basePackage);   // (a prop: the character's component and animations aren't this build's)
        string? pkgOut = WritePackage(basePackage, export);
        if (pkgOut == null) return null;
        if ((rigs.Count > 0 || o.AnimFbx is { Count: > 0 }) && !HairAnimation(pkgOut)) return null;
        var result = o.NoMod ? new Result(pkgOut, "", "", "") : WriteMod(pkgOut);
        CheckReadBack(native);
        return result;
    }

    // --- retarget and the base mesh ---------------------------------------------------------------------------------------
    void Prepare()
    {
        sk = MhoSkeleton.Load(package, null);
        baseBones = sk.Bones.Count;
        if (o.SourceFbx != null)
        {
            sourceName = SourceName(o.SourceFbx);
            log("source: " + o.SourceFbx);
            var only = o.Parts is { Length: > 0 } ps && ps != "default" && ps != "all" ? ps.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            if (PropFit.IsProp(sk)) throw new InvalidOperationException($"{sk.Name} is a prop (a weapon or shield): for now a prop can only be built from an MFF character's part");
            r = FbxReimport.Load(o.SourceFbx, sk, only, log);
            // an FBX source's map holds only its weight smoothing (the Bone Map's Smooth Weights)
            if (o.MapFile != null) foreach (var n in WeightSmooth.Apply(r, BoneMapFile.Load(o.MapFile).Smooth)) log("  " + n);
        }
        else
        {
            m = MffModel.Load(Source.ResolveModelFile(mff));
            sourceName = m.Folder;
            if (o.Cape > 0)
            {
                (sk, var capeRig) = BorrowedRig.Graft(sk, BorrowedRig.Kind.Cape, o.Cape, out string note, 0);
                log("cape:    " + note);
                if (capeRig != null) rigs.Add(capeRig);
            }
            if (o.Hair > 0)
            {
                (sk, hairRig) = BorrowedRig.Graft(sk, BorrowedRig.Kind.Hair, o.Hair, out string note, BorrowedRig.HairDrop(m));
                log("hair:    " + note);
                if (hairRig != null) rigs.Add(hairRig);
            }
            var picked = m.Selected(o.Parts);
            // a prop target (a weapon or shield picked under Model ▾): with no parts named, the part most like it
            if (PropFit.IsProp(sk) && o.Parts is null or "default" && PropFit.BestPart(m, sk) is { } best) { picked = [best]; log($"prop:    {sk.Name} is a prop: built from the MFF part most like it, {best.Name}"); }
            if (o.Subdivide) picked = Subdivision.Apply(picked);
            r = Retarget.Run(m, picked, sk, o.MapFile != null ? BoneMapFile.Load(o.MapFile) : null);
            foreach (var n in r.Notes.Where(n => n.StartsWith("prop:", StringComparison.Ordinal))) log(n.Replace("prop: ", "prop:    "));
        }
        if (o.ModelFbx != null) { log("mesh from the edited FBX: " + o.ModelFbx); FbxReimport.Apply(r, o.ModelFbx, log); }
        if ((o.Hair > 0 || o.Cape > 0) && o.SourceFbx != null) log("borrow:  an FBX source keeps its own bones: the borrowed cape / hair is left out");
        MaterialOverrides.Apply(r, o.MaterialOverrides, line => log("  " + line));   // the Materials tab's files
        builtBones = MhoAnim.FromGlobals(r.Bones.Select(b => (b.Name, b.Parent, b.Global)).ToList());
        foreach (var rig in rigs)
        {
            rig.Paired = r.ChainPairs.Count(c => BorrowedRig.Pattern(rig.Part).IsMatch(c.MhoRoot));
            rig.MeasureBody(builtBones, r);
            log($"{(rig.Part == BorrowedRig.Kind.Cape ? "cape:  " : "hair:  ")}  {rig.Paired} model chain(s) ride {rig.Label}");
        }
        pkg = AnimPackage.Open(package);
        int e = MeshExport(pkg);
        var props = pkg.TryReadProperties(e)!;
        data = pkg.GetExportData(e).ToArray();
        payloadOffset = props.PayloadOffset;
        baseN = SkelNative.Read(data, props.PayloadOffset, props.GetBool("bhasvertexcolors"));
        // Sections by MFF material.
        mats = r.Sections.Select(x => x.Material).Distinct().ToList();
        enc = r.Sections.Select(x => new EncSection
        {
            Material = mats.IndexOf(x.Material), Pos = x.Pos, Normal = x.Normal, Tris = x.Tris, Weights = x.Weights,
            Uv = x.Uv.Select(u => new Vector2(u.X, 1 - u.Y)).ToArray(),   // FBX bottom-left → texture top-left
        }).ToList();
    }

    /// <summary>"hero_x on UC__..." for data\fbx\hero_x on UC__...\model.fbx, else the file's name.</summary>
    public static string SourceName(string fbx)
    {
        string stem = Path.GetFileNameWithoutExtension(fbx);
        string folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(fbx)) ?? "") ?? "";
        if (!stem.Equals("model", StringComparison.OrdinalIgnoreCase) || folder.Length == 0) return stem;
        int on = folder.IndexOf(" on ", StringComparison.Ordinal);
        return on > 0 ? folder[..on] : folder;
    }

    int MeshExport(AnimPackage p) => p.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName).First(i => p.GetExportName(i).Equals(sk.Name, StringComparison.OrdinalIgnoreCase));

    // --- materials ------------------------------------------------------------------------------------------------------------
    /// <summary>One material instance per MFF material (a renamed copy of the chosen template; textures from the MFF colour /
    /// _sp maps and the generated normal map). Placeholder mode keeps the base mesh's own materials, in turn.</summary>
    (byte[] Package, List<int> MatRefs) Materials()
    {
        var matRefs = mats.Select((_, i) => baseN.Materials[i % baseN.Materials.Count]).ToList();
        byte[] basePackage = File.ReadAllBytes(package);
        if (o.PlaceholderMaterials) return (basePackage, matRefs);

        var mp = MpmPackage.FromBytes(basePackage);
        int firstMat = baseN.Materials.FirstOrDefault(x => x > 0 && mp.ClassOf(mp.Exports[x - 1]).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase));
        if (firstMat <= 0) throw new InvalidDataException("the base mesh has no material instance of its own to copy (every one is imported)");
        string template = mp.Exports[firstMat - 1].ObjectName;
        metalShare = MaterialChoice.MetalShare(r.Sections.Select(x => x.Tex.Spec).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
        // an MHO packed spec map: Angela's own armor material reads all of it (shine, power, skin mask, reflectivity) and a spec color
        if (r.Sections.Any(x => x.Tex.UsesMhoSpec)) { metalShare = 1; log("material: an MHO spec map is in use: the Metal template (Angela's armor material) reads all four of its channels and the spec color"); }
        glowShare = metalShare > 0.5f && !r.Sections.Any(x => x.Tex.UsesMhoSpec) ? MaterialChoice.GlowShare(r.Sections.Select(x => x.Tex.Diffuse).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)) : 0;
        // a glow map of its own on any material (its file, or an MHO spec map's glow channel): Angela's weapon material, her
        // armor's with a glow slot (the same packed spec and spec color slots); the others get no glow
        bool ownGlow = r.Sections.Any(x => x.Tex.GlowFile != null);
        string? donor = o.Material == null && ownGlow ? MaterialChoice.Glow : MaterialChoice.Donor(o.Material, metalShare, glowShare);
        if (ownGlow && o.Material == null) log($"material: glow maps on {string.Join(", ", r.Sections.Where(x => x.Tex.GlowFile != null).Select(x => x.Material).Distinct())}: Angela's weapon material (her armor's, with a glow slot)");
        log($"material: {metalShare:P0} of the _sp maps marks metal, glow spots on {glowShare:P2} of the colour maps → {(donor == null ? "the base mesh's own material" : donor.Split(':').Last())}{(o.Material != null ? " (MFF_MATERIAL)" : " (automatic)")}");
        // a package that already holds a Model build (made elsewhere: no copy from before it was kept): this build's objects
        // get their own names, next to the earlier one's
        string namePrefix = MaterialOut.PrefixFor(mp);
        if (namePrefix != "mff_") log($"material: the package already holds an earlier Model build (its objects are kept, unused); this build's are named {namePrefix}…");
        if (donor != null)
        {
            template = namePrefix + "template_mat";
            basePackage = MaterialChoice.CopyDonor(mp, donor, o.ValuesFrom, o.Glow, log, template);
        }
        string texDir = Path.Combine(outDir, "textures");
        Protected.CheckWrite(texDir);
        Directory.CreateDirectory(texDir);
        bool guessGlow = donor == MaterialChoice.Glow && !ownGlow;
        var mffMats = mats.Select(name => MffMaterialFor(name, texDir) is var mm && mm.Glow == null && guessGlow && !r.Sections.First(x => x.Material == name).Tex.GlowOff ? mm with { GuessGlow = true } : mm).ToList();
        var madeMats = MaterialOut.Build(basePackage, template, mffMats, texDir, o.Spec, o.Reflect, namePrefix);
        foreach (var n in madeMats.Notes) log("material: " + n);
        return (madeMats.Package, mats.Select(x => madeMats.MaterialRef[x]).ToList());
    }

    /// <summary>An MFF material's maps: colour, _sp (left out for Spec "neutral"), and a normal map generated from the colour
    /// map (none for Normal "flat"; a number = its strength).</summary>
    MffMaterial MffMaterialFor(string name, string texDir)
    {
        var tex = r.Sections.First(x => x.Material == name).Tex;
        string? normal = null;
        var nset = new NormalMapSettings();
        if (o.Normal != null && float.TryParse(o.Normal, NumberStyles.Float, CultureInfo.InvariantCulture, out float ns)) nset.Strength = ns;
        if (tex.Normal != null && o.Normal != "flat")
        {
            // the model's own normal map (Kurt, 2026-10-04), as DirectX green PNG
            normal = MaterialOverrides.NormalForGame(tex.Normal, tex.NormalFlipGreen, texDir, MaterialOut.Safe(name) + "_own");
            log($"material: {name}: normal map from {Path.GetFileName(tex.Normal)}{(tex.NormalFlipGreen ? " (OpenGL: green flipped)" : "")}");
        }
        else if (tex.Diffuse != null && o.Normal != "flat")
        {
            log($"material: {name}: no normal map of its own: one is generated from the color map");
            var (w, h, px) = NormalMapGen.LoadArgb(tex.Diffuse);
            int[]? mask = null;
            if (tex.Alpha != null) { var (aw, ah, apx) = NormalMapGen.LoadArgb(tex.Alpha); if (aw == w && ah == h) mask = apx; }
            normal = Path.Combine(texDir, MaterialOut.Safe(name) + "_n.png");
            NormalMapGen.SaveArgb(w, h, NormalMapGen.Make(w, h, px, nset, mask), normal);
        }
        // no spec map of its own: one generated from the color map (SpecMapGen, measured on the game's own maps)
        string? spec = o.Spec == "neutral" ? null : tex.Spec;
        bool generated = false;
        if (spec == null && o.Spec != "neutral" && tex.Diffuse != null && !tex.UsesMhoSpec)
        {
            spec = SpecMapGen.Write(tex.Diffuse, tex.Alpha, tex.SpecRecipe, texDir, MaterialOut.Safe(name));
            generated = true;
            log($"material: {name}: no spec map of its own: generated from the color map, {SpecMapGen.Label(tex.SpecRecipe)}");
        }
        string? specMho = tex.SpecMhoAngela;
        if (tex.SpecMho != null && tex.SpecLayoutUsed.Id != "v2skin")
            log($"material: {name}: MHO spec map in the {tex.SpecLayoutUsed.Label} layout, {SpecLayouts.Change(tex.SpecLayoutUsed)}");
        if (specMho == null && tex.ColorTags is { Count: > 0 } tags && tex.Diffuse != null)
        {
            // the user's color group tags: an MHO packed map made from them (ColorTags)
            specMho = ColorTags.Write(tex.TagsColor!, tex.Alpha, tags, texDir, MaterialOut.Safe(name));
            log($"material: {name}: spec map made from your color tags ({tags.Count} group(s): {string.Join(", ", tags.GroupBy(t => ColorTags.Name(t.Tag)).Select(g => $"{g.Count()} {ColorTags.Label(g.Key).ToLowerInvariant()}"))})");
        }
        if (specMho != null) { spec = null; generated = false; }   // an MHO packed map wins (as it is)
        string? glow = tex.GlowFile;
        if (glow != null) log($"material: {name}: glow map {(tex.Glow != null && !tex.GlowOff ? Path.GetFileName(tex.Glow) : $"from the MHO spec map's {tex.SpecLayoutUsed.Channels[tex.SpecLayoutUsed.GlowChannel]} channel")}");
        return new MffMaterial(name, tex.Diffuse, spec, normal, generated) { SpecMho = specMho, SpecColor = tex.SpecColor, Glow = glow };
    }

    // --- the mesh export --------------------------------------------------------------------------------------------------------
    SkelNative built = null!;

    /// <summary>The base mesh's properties (lodinfo's per-section arrays sized for the new sections: the renderer indexes them
    /// by section) + the new native data; also written to &lt;mesh&gt;.skelmesh.bin.</summary>
    (byte[] Export, byte[] Native) EncodeMesh(List<int> matRefs, List<(long Name, int Parent)>? extra)
    {
        var locals = builtBones.Select(b => (b.Orientation, b.Position)).ToList();
        built = SkelEncoder.Build(baseN, locals, enc, matRefs, extra);
        if (extra != null) log($"bones:   {baseN.Bones.Count} + {extra.Count} borrowed ({string.Join(", ", sk.Bones.Skip(baseN.Bones.Count).Select(b => b.Name))}), skeletal depth {built.SkeletalDepth} (stock {baseN.SkeletalDepth})");
        var native = built.Write();
        int pat = 4;
        var tprops = TaggedProps.Read(data, ref pat, Program.Names(pkg));
        if (pat != payloadOffset) throw new InvalidDataException($"properties end at {pat}, the reader says {payloadOffset}");
        foreach (var c in TaggedProps.ResizeLodSectionArrays(tprops, built.Lods[0].Sections.Count)) log($"props:   {c}");
        var propBytes = data.AsSpan(0, 4).ToArray().Concat(tprops.Write()).ToArray();
        var export = propBytes.Concat(native).ToArray();
        string outFile = Path.Combine(outDir, sk.Name + ".skelmesh.bin");
        Protected.CheckWrite(outFile);
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(outFile, export);
        log($"encoded: {built.Lods[0].VbCount} vertices, {built.Lods[0].Indices.Count / 3} triangles, {built.Lods[0].Sections.Count} sections ({mats.Count} materials), {built.Lods[0].Chunks.Max(c => c.BoneMap.Count)} bones in the largest chunk, index width {built.Lods[0].IndexWidth}");
        log($"export:  {export.Length} bytes (properties {propBytes.Length}, stock {payloadOffset}; native {native.Length}, stock {data.Length - payloadOffset}) → {Path.GetFullPath(outFile)}");
        return (export, native);
    }

    // --- borrowed hair -------------------------------------------------------------------------------------------------------
    /// <summary>The grafted bones' names added to the package's name table (the bone records refer to them by index), and
    /// each one's name reference and parent; null without grafted bones. Adding names keeps every existing index.</summary>
    List<(long Name, int Parent)>? HairNames(ref byte[] basePackage)
    {
        if (sk.Bones.Count == baseBones) return null;
        if (baseN.Bones.Count != baseBones) throw new InvalidDataException($"the base mesh has {baseN.Bones.Count} bones, its skeleton {baseBones}");
        var src = MhoPackageModifier.Package.FromBytes(basePackage);
        var names = sk.Bones.Skip(baseBones).Select(b => b.Name).ToList();
        var add = names.Where(n => !src.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (add.Count > 0)
        {
            byte[] bytes = MhoPackageModifier.PackageRebuilder.Rebuild(src, new Dictionary<int, Func<long, byte[]>>(), [], out var written, add);
            var problems = MhoPackageModifier.PackageRebuilder.Verify(src, bytes, [], [], written, add);
            if (problems.Count > 0) throw new InvalidDataException("adding the hair bone names: " + string.Join("; ", problems.Take(3)));
            basePackage = bytes;
            src = MhoPackageModifier.Package.FromBytes(bytes);
        }
        return sk.Bones.Skip(baseBones).Select(b => ((long)Array.FindIndex(src.Names, x => x.Equals(b.Name, StringComparison.OrdinalIgnoreCase)), b.ParentIndex)).ToList();
    }

    /// <summary>The hair's motion into the written package (<see cref="HairAnims"/>), verified and read back; false (logged)
    /// when it couldn't be done: then no mod is made, so a half-built one can't be installed.</summary>
    bool HairAnimation(string pkgOut)
    {
        try
        {
            var res = HairAnims.Add(pkgOut, Path.GetFileName(package), builtBones, sk.Borrowed, rigs, log, o.AnimFbx);
            Protected.CheckWrite(pkgOut);
            File.WriteAllBytes(pkgOut, res.Package);
            log($"anims:   {res.Sets} animation set(s), {res.Sequences} animations written{(rigs.Count > 0 ? $" with {string.Join(" and ", rigs.Select(x => x.Label))}'s motion" : "")}{(o.AnimFbx is { Count: > 0 } af ? $", {af.Count} replaced from FBX edits" : "")}{(res.Static > 0 ? $" ({res.Static} with the hair at rest: not decodable or a packed format)" : "")}; package {new FileInfo(pkgOut).Length:N0} bytes");
            return true;
        }
        catch (InvalidDataException ex) { log("PACKAGE PROBLEM: animations: " + ex.Message); return false; }
    }

    // --- morph targets ---------------------------------------------------------------------------------------------------------
    /// <summary>
    /// The character's mesh component without morph sets (0.14.1, Kurt: Scream on Carnage distorted in fidget02 in game, not in
    /// the preview). A morph target (shape key) stores offsets by the stock mesh's vertex numbers; Carnage's 20 (arm blades,
    /// mace, axe: carnage_morphtargetset) are played by his animations and powers, and on the new mesh those numbers are other
    /// vertices, pulled out into sheets. The component's MorphSets (default__marvel….initialskeletalmesh) becomes an empty
    /// list, so the game has no morph to apply to the new mesh; the morph exports stay (nothing else changes). Other
    /// components (the scythe props' own meshes) keep theirs.
    /// </summary>
    byte[] NoMorphs(byte[] packageBytes)
    {
        var src = MhoPackageModifier.Package.FromBytes(packageBytes);
        for (int i = 0; i < src.Exports.Length; i++)
        {
            var e = src.Exports[i];
            if (!src.ClassOf(e).Equals("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            string path = src.PathOf(e);
            if (!path.EndsWith(".initialskeletalmesh", StringComparison.OrdinalIgnoreCase) || !path.Contains("default__marvel", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = src.ReadExportBytes(e).ToArray();
            int at = 16;
            TaggedProps props;
            var names = src.Names;
            try { props = TaggedProps.Read(d, ref at, r => TaggedProps.NameOf(names, r)); }
            catch (InvalidDataException) { log($"morphs:  {path}: properties not read, left as it is"); continue; }
            var tag = props.Find("morphsets");
            if (tag == null || tag.Value.Length < 4 || BitConverter.ToInt32(tag.Value, 0) == 0) continue;
            int count = BitConverter.ToInt32(tag.Value, 0);
            tag.Value = new byte[4];   // an empty list
            byte[] rebuilt = [.. d.AsSpan(0, 16), .. props.Write(), .. d.AsSpan(at)];
            var bytes = PackageOut.ReplaceExport(src, i, rebuilt);
            var problems = PackageOut.Verify(src, MhoPackageModifier.Package.FromBytes(bytes), i, rebuilt);
            if (problems.Count > 0) { foreach (var x in problems) log("morphs:  PACKAGE PROBLEM: " + x); continue; }
            log($"morphs:  {path}: its {count} morph set(s) removed (they move the stock mesh's vertices; on the new mesh they'd pull other vertices)");
            packageBytes = bytes;
            src = MhoPackageModifier.Package.FromBytes(bytes);
        }
        return ApplySize(TurnOnlySets(InheritedMorphs(packageBytes)));
    }

    /// <summary>The positions every player AnimSet keeps (--anim-probe on Storm, Thor, Daredevil, Spider-Man, Hulk: these 16,
    /// plus cape / hair / muscle bones of their own).</summary>
    static readonly string[] CorePositionBones = ["g_spine01", "g_spine02", "g_spine03", "g_topeyelid", "g_l_hip", "g_r_hip", "g_l_legikeffector",
        "g_r_legikeffector", "g_l_armikeffector", "g_r_armikeffector", "g_armikbase", "g_breast", "g_l_palm", "g_r_palm", "g_throwable_attach_offset", "g_throwable_attach"];

    /// <summary>An animation as the build leaves it (TurnOnlySets): one from a set with positions for every bone and a body
    /// turns bones only, positions for the core bones; the preview of a fitted model shows it so.</summary>
    public static AnimRef AsBuilt(AnimRef a) =>
        a.TranslationBones != null || !a.TrackBoneNames.Contains("g_l_shoulder", StringComparer.OrdinalIgnoreCase) ? a
            : a with { TranslationBones = new HashSet<string>(CorePositionBones.Where(b => a.TrackBoneNames.Contains(b, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase) };

    /// <summary>
    /// AnimSets that set every bone's position (Kurt, 2026-10-06: an MFF Ms. Marvel on the Kamala Khan team-up had distorted
    /// arms and shoulders). Player AnimSets turn bones only (bAnimRotationOnly, omitted = true) and keep positions for a few
    /// (UseTranslationBoneNames), so a model fitted here, whose bones moved to its own proportions, keeps them. Team-up (and
    /// some NPC) sets store bAnimRotationOnly = false: every frame puts each bone back where the stock model has it, and the new
    /// skin, bound to the moved bones, stretches. Such a set (one with a body: g_l_shoulder among its tracks) is made like a
    /// player's: rotation only, positions for the 16 core bones it has. The package is rebuilt and verified.
    /// </summary>
    byte[] TurnOnlySets(byte[] packageBytes)
    {
        var src = MhoPackageModifier.Package.FromBytes(packageBytes);
        var names = src.Names;
        for (int i = 0; i < src.Exports.Length; i++)
        {
            var e = src.Exports[i];
            if (!src.ClassOf(e).Equals("AnimSet", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = src.ReadExportBytes(e).ToArray();
            TaggedProps? props = null;
            int at = 0, start = 0;
            foreach (int off in new[] { 4, 0, 8, 16 })
            {
                at = off;
                try { var t = TaggedProps.Read(d, ref at, r => TaggedProps.NameOf(names, r)); if (t.Find("TrackBoneNames") != null) { props = t; start = off; break; } }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
            }
            if (props == null) continue;
            var flag = props.Find("bAnimRotationOnly");
            if (flag == null || flag.BoolValue != 0) continue;
            var track = props.Find("TrackBoneNames")!;
            int n = BitConverter.ToInt32(track.Value, 0);
            var bones = new List<(int Index, int Number)>();
            for (int k = 0; k < n && 4 + 8 * k + 8 <= track.Value.Length; k++)
                bones.Add((BitConverter.ToInt32(track.Value, 4 + 8 * k), BitConverter.ToInt32(track.Value, 8 + 8 * k)));
            string BoneName((int Index, int Number) b) => b.Index >= 0 && b.Index < names.Length ? names[b.Index] : "";
            if (!bones.Any(b => BoneName(b).Equals("g_l_shoulder", StringComparison.OrdinalIgnoreCase))) continue;
            var keep = bones.Where(b => b.Number == 0 && CorePositionBones.Contains(BoneName(b), StringComparer.OrdinalIgnoreCase)).ToList();
            var list = props.Find("UseTranslationBoneNames");
            if (list == null) { log($"anim:    {src.PathOf(e)}: sets every bone's position but has no UseTranslationBoneNames to fill; left as it is (arms may stretch)"); continue; }
            flag.BoolValue = 1;
            var v = new byte[4 + 8 * keep.Count];
            BitConverter.GetBytes(keep.Count).CopyTo(v, 0);
            for (int k = 0; k < keep.Count; k++) { BitConverter.GetBytes(keep[k].Index).CopyTo(v, 4 + 8 * k); BitConverter.GetBytes(keep[k].Number).CopyTo(v, 8 + 8 * k); }
            int before = list.Value.Length >= 4 ? BitConverter.ToInt32(list.Value, 0) : 0;
            list.Value = v;
            byte[] rebuilt = [.. d.AsSpan(0, start), .. props.Write(), .. d.AsSpan(at)];
            var bytes = PackageOut.ReplaceExport(src, i, rebuilt);
            var problems = PackageOut.Verify(src, MhoPackageModifier.Package.FromBytes(bytes), i, rebuilt);
            if (problems.Count > 0) { foreach (var x in problems) log("anim:    PACKAGE PROBLEM: " + x); continue; }
            log($"anim:    {src.PathOf(e)}: set every bone's position ({n} bones, {before} listed); now turns bones only, positions for {keep.Count} core bones, as player sets do (the new model keeps its own proportions)");
            packageBytes = bytes;
            src = MhoPackageModifier.Package.FromBytes(bytes);
            names = src.Names;
        }
        return packageBytes;
    }

    /// <summary>
    /// The Model tab's Size (Kurt, 2026-10-05: larger or smaller in game, the animations undistorted): the costume's mesh
    /// component gets a Scale, as the game's own costumes do (--tag-census on the stock packages: 21 player components set one,
    /// e.g. Spider-Man Homecoming 0.975, Wolverine 1.05, Carnage 1.1, Ultron AoU 0.938). The engine scales the posed skeleton as
    /// a whole, so the animations keep their shape; the root is at the feet, so they stay on the ground. Size is relative to
    /// the game's: the component's own Scale, else its hero's base component's, else 1.
    /// </summary>
    byte[] ApplySize(byte[] packageBytes) =>
        Math.Abs(o.Size - 1) < 1e-4 && o.SizeFrom == null ? packageBytes
            : Resize(packageBytes, o.SizeFrom != null && File.Exists(o.SizeFrom) ? File.ReadAllBytes(o.SizeFrom) : packageBytes, Path.GetFileName(package), o.Size, log) ?? packageBytes;

    /// <summary>
    /// The size written into <paramref name="current"/> (the package as the mod has it, a model built in or not): the game's
    /// Scale (read from <paramref name="start"/>, the package before the Model tab changed it: its component's own, else its
    /// hero's base component's, else 1) times <paramref name="size"/>, so sizing again never compounds; 1 puts the game's back.
    /// Null when the package has no mesh component of its own (a costume that uses its hero's, such as Carnage Classic: the
    /// log says to size the hero's base package instead).
    /// </summary>
    public static byte[]? Resize(byte[] current, byte[] start, string packageName, float size, Action<string> log)
    {
        string stem = Path.GetFileNameWithoutExtension(packageName);
        if (stem.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) stem = stem[4..];
        if (stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
        string compPath = $"marvelgamecontent.default__{stem.ToLowerInvariant()}.initialskeletalmesh";
        static float? OwnScale(MhoPackageModifier.Package p, int comp)
        {
            byte[] bytes = p.ReadExportBytes(p.Exports[comp]).ToArray();
            var t = MhoPackageModifier.TagWalker.Walk(p, bytes, 16)?.FirstOrDefault(x => x.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase));
            return t != null ? BitConverter.ToSingle(bytes, t.ValueAt) : null;
        }
        var src = MhoPackageModifier.Package.FromBytes(current);
        int comp = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0)
        {
            string[] p = stem.Split('_');
            log(p.Length >= 3 && p[0].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase)
                ? $"size:    {packageName} has no mesh component of its own: this costume uses its hero's, in UC__MarvelPlayer_{p[1]}_SF. To size it, pick that package (Target → From the Game, the hero's Base Package): that sizes every {p[1]} costume that doesn't set a size of its own."
                : $"size:    no {compPath} in {packageName}: the size is left as the game's");
            return null;
        }
        var sp = MhoPackageModifier.Package.FromBytes(start);
        int sc = Array.FindIndex(sp.Exports, e => sp.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        float stock = (sc >= 0 ? OwnScale(sp, sc) : null) ?? HeroScale(stem);
        float want = stock * size;
        byte[] d = src.ReadExportBytes(src.Exports[comp]).ToArray();
        var tags = MhoPackageModifier.TagWalker.Walk(src, d, 16);
        if (tags == null) { log($"size:    {compPath}: its properties don't read; the size is left as the game's"); return null; }
        var own = tags.FirstOrDefault(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase));
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(src.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return src.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        byte[] rebuilt;
        if (own != null) { rebuilt = (byte[])d.Clone(); BitConverter.GetBytes(want).CopyTo(rebuilt, own.ValueAt); }
        else
        {
            var tag = new byte[28];   // name, type, size 4, array index 0, the value
            BitConverter.GetBytes(NameIdx("Scale")).CopyTo(tag, 0);
            BitConverter.GetBytes(NameIdx("FloatProperty")).CopyTo(tag, 8);
            BitConverter.GetBytes(4).CopyTo(tag, 16);
            BitConverter.GetBytes(want).CopyTo(tag, 24);
            rebuilt = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
        }
        var replace = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => rebuilt };
        byte[] output = MhoPackageModifier.PackageRebuilder.Rebuild(src, replace, [], out var written, addNames);
        var problems = MhoPackageModifier.PackageRebuilder.Verify(src, output, replace.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { foreach (var x in problems.Take(3)) log("size:    PACKAGE PROBLEM: " + x); return null; }
        var back = MhoPackageModifier.Package.FromBytes(output);
        if (OwnScale(back, comp) is not float got || Math.Abs(got - want) > 1e-6) { log("size:    PACKAGE PROBLEM: the Scale didn't read back"); return null; }
        log($"size:    {compPath}: Scale {(own != null ? BitConverter.ToSingle(d, own.ValueAt) : stock):0.###} → {want:0.###} ({size * 100:0} % of the game's {stock:0.###})");
        return output;
    }

    /// <summary>The Scale a costume inherits: its hero's base component's (UC__MarvelPlayer_&lt;Hero&gt;_SF), else 1.</summary>
    static float HeroScale(string stem)
    {
        string[] p = stem.Split('_');
        if (p.Length < 3 || !p[0].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase)) return 1;
        try
        {
            string hero = p[1];
            var bp = MhoPackageModifier.Package.Open(BasePackage.Resolve($"UC__MarvelPlayer_{hero}_SF.upk", true));
            int bc = Array.FindIndex(bp.Exports, e => bp.PathOf(e).Equals($"marvelgamecontent.default__marvelplayer_{hero.ToLowerInvariant()}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
            if (bc < 0) return 1;
            byte[] bd = bp.ReadExportBytes(bp.Exports[bc]).ToArray();
            var t = MhoPackageModifier.TagWalker.Walk(bp, bd, 16)?.FirstOrDefault(x => x.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase));
            return t != null ? BitConverter.ToSingle(bd, t.ValueAt) : 1;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidDataException) { return 1; }
    }

    /// <summary>
    /// Morph sets a costume inherits (0.17.1): a costume's component that doesn't set MorphSets takes its hero's (the hero's base
    /// component is its template). `--morph-survey` on the stock packages: only Rogue's five Ultimate Form packages do (Rogue's
    /// base lists 1 set); every other costume of a morph hero (Carnage, Mr Fantastic, Venom, Rogue) sets its own, emptied
    /// above. Here such a component gets an explicit empty MorphSets list (a tag before its None), which overrides the inherited
    /// one; the package is rebuilt with the names it needs and verified.
    /// </summary>
    byte[] InheritedMorphs(byte[] packageBytes)
    {
        string own = Path.GetFileNameWithoutExtension(package);
        if (!own.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || !own.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) return packageBytes;
        string stem = own["UC__MarvelPlayer_".Length..^3];
        if (!stem.Contains('_')) return packageBytes;   // a base package: its own sets were emptied above
        string hero = stem.Split('_')[0];
        var src = MhoPackageModifier.Package.FromBytes(packageBytes);
        string compPath = $"marvelgamecontent.default__marvelplayer_{stem.ToLowerInvariant()}.initialskeletalmesh";
        int comp = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0) return packageBytes;
        byte[] d = src.ReadExportBytes(src.Exports[comp]).ToArray();
        var tags = MhoPackageModifier.TagWalker.Walk(src, d, 16);
        if (tags == null || tags.Any(t => t.Name.Equals("MorphSets", StringComparison.OrdinalIgnoreCase))) return packageBytes;
        // the hero's: does its base component list any?
        int heroSets = 0;
        try
        {
            string basePath = BasePackage.Resolve($"UC__MarvelPlayer_{hero}_SF.upk", true);
            var bp = MhoPackageModifier.Package.Open(basePath);
            int bc = Array.FindIndex(bp.Exports, e => bp.PathOf(e).Equals($"marvelgamecontent.default__marvelplayer_{hero.ToLowerInvariant()}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
            if (bc >= 0)
            {
                byte[] bd = bp.ReadExportBytes(bp.Exports[bc]).ToArray();
                if (MhoPackageModifier.TagWalker.Walk(bp, bd, 16)?.FirstOrDefault(t => t.Name.Equals("MorphSets", StringComparison.OrdinalIgnoreCase)) is { } bt) heroSets = BitConverter.ToInt32(bd, bt.ValueAt);
            }
        }
        catch (FileNotFoundException) { return packageBytes; }
        if (heroSets <= 0) return packageBytes;
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(src.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return src.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var tag = new byte[28];   // name, type, size 4, array index 0, then the value: count 0
        BitConverter.GetBytes(NameIdx("MorphSets")).CopyTo(tag, 0);
        BitConverter.GetBytes(NameIdx("ArrayProperty")).CopyTo(tag, 8);
        BitConverter.GetBytes(4).CopyTo(tag, 16);
        byte[] rebuilt = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
        var replace = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => rebuilt };
        byte[] output = MhoPackageModifier.PackageRebuilder.Rebuild(src, replace, [], out var written, addNames);
        var problems = MhoPackageModifier.PackageRebuilder.Verify(src, output, replace.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { foreach (var x in problems.Take(3)) log("morphs:  PACKAGE PROBLEM: " + x); return packageBytes; }
        // read back: the component now lists none
        var back = MhoPackageModifier.Package.FromBytes(output);
        byte[] bd2 = back.ReadExportBytes(back.Exports[comp]).ToArray();
        var bt2 = MhoPackageModifier.TagWalker.Walk(back, bd2, 16)?.FirstOrDefault(t => t.Name.Equals("MorphSets", StringComparison.OrdinalIgnoreCase));
        if (bt2 == null || BitConverter.ToInt32(bd2, bt2.ValueAt) != 0) { log("morphs:  PACKAGE PROBLEM: the empty MorphSets didn't read back"); return packageBytes; }
        log($"morphs:  {compPath}: inherited {heroSets} morph set(s) from {hero}'s base package; an empty list of its own now overrides them");
        return output;
    }

    // --- the package --------------------------------------------------------------------------------------------------------------
    /// <summary>A copy of the base package with this mesh in place (MPM's PackageWriter), verified, then the mesh and its
    /// properties read back with AnimExportCli's reader. Null when the package check failed.</summary>
    string? WritePackage(byte[] basePackage, byte[] export)
    {
        string pkgOut = Path.Combine(outDir, Path.GetFileName(package));
        var wp = PackageOut.WriteMesh(basePackage, Path.GetFileName(package), sk.Name, export, pkgOut);
        if (wp.Count > 0) { foreach (var x in wp) log("PACKAGE PROBLEM: " + x); return null; }
        var rp = AnimPackage.Open(pkgOut);
        int re = MeshExport(rp);
        string why = "";
        var rm = AnimExportCli.Meshes.SkeletalMeshReader.TryRead(rp, re, x => why = x);
        log(rm == null ? $"PACKAGE PROBLEM: the mesh in the written package doesn't read: {why}"
            : $"package: {Path.GetFullPath(pkgOut)} ({new FileInfo(pkgOut).Length:N0} bytes, uncompressed; stock {new FileInfo(package).Length:N0} compressed): verified; mesh reads back: {rm.Bones.Count} bones, {rm.HighestDetail!.Positions.Count} vertices, {rm.HighestDetail.TriangleCount} triangles, {rm.HighestDetail.Sections.Count} sections");
        var rprops = rp.TryReadProperties(re);
        int ra = 4; var rt = TaggedProps.Read(rp.GetExportData(re).ToArray(), ref ra, Program.Names(rp));
        var lod0 = rt.StructArray(rt.Find("lodinfo")!)[0];
        log($"package: properties read back (payload at {rprops?.PayloadOffset}, tags end at {ra}); lodinfo[0] shadow casting {BitConverter.ToInt32(lod0.Find("benableshadowcasting")!.Value)}, triangle sort {BitConverter.ToInt32(lod0.Find("trianglesortsettings")!.Value)} for {rm?.HighestDetail?.Sections.Count} sections");
        return pkgOut;
    }

    // --- the Mod Manager mod --------------------------------------------------------------------------------------------------
    /// <summary>The importer's output: a Mod Manager mod (manifest + the package) and its .zip beside it.</summary>
    Result WriteMod(string pkgOut)
    {
        string modDir = Path.Combine(outDir, "mod");
        string variant = o.Variant(MaterialChoice.Donor(o.Material, metalShare, glowShare));
        string modName = $"{(m != null ? "MFF " : "FBX ")}{sourceName} on {sk.Name} (test{variant})";
        ModOut.Write(modDir, modName, "MHO MFF Importer", Program.Version, [pkgOut],
            $"Test build of the MFF Importer {Program.Version}: {sourceName} on {Path.GetFileName(package)}. " +
            (o.PlaceholderMaterials ? "Materials are the stock ones (placeholders)." : $"Materials from the MFF textures (A/B variant:{(variant.Length > 0 ? variant.TrimStart(',') : " colour, _sp, generated normal map")})."));
        string zip = ModOut.Zip(modDir, modName, Program.Version);
        log($"mod:     {Path.GetFullPath(modDir)} (\"{modName}\")");
        log($"zip:     {zip} ({new FileInfo(zip).Length:N0} bytes, read back and checked): install it with the Mod Manager, then Apply");
        return new Result(pkgOut, modDir, zip, modName);
    }

    // --- checks -------------------------------------------------------------------------------------------------------------------
    /// <summary>The written native data decoded and compared with the retarget (every corner: position, UV, weights; bones;
    /// bounds), then an animation frame rendered from the decoded bytes beside the retarget's own pose.</summary>
    void CheckReadBack(byte[] native)
    {
        var back = SkelNative.Read(native, 0, false);
        var (p2, n2, t2, u2, w2, _) = SkelEncoder.Decode(back.Lods[0]);
        int at = 0; float dp = 0, du = 0, dw = 0; int boneBad = 0, count = 0;
        foreach (var x in enc)
            foreach (int v in x.Tris)
            {
                int nv = back.Lods[0].Indices[at++];
                dp = MathF.Max(dp, (p2[nv] - x.Pos[v]).Length());
                du = MathF.Max(du, (u2[nv] - x.Uv[v]).Length());
                var a2 = w2[nv].ToDictionary(k => k.Item1, k => k.Item2);
                float tot = x.Weights[v].Sum(k => k.Weight);
                foreach (var (b, wt) in x.Weights[v]) { if (!a2.ContainsKey(b)) boneBad++; else dw = MathF.Max(dw, MathF.Abs(a2[b] - wt / tot)); }
                count++;
            }
        var decodedBones = back.Bones.Select((b, i) => new AnimExportCli.Meshes.MeshBone
        {
            Name = r.Bones[i].Name, ParentIndex = BitConverter.ToInt32(b, 44) == i ? -1 : BitConverter.ToInt32(b, 44),
            Orientation = new Quaternion(BitConverter.ToSingle(b, 12), BitConverter.ToSingle(b, 16), BitConverter.ToSingle(b, 20), BitConverter.ToSingle(b, 24)),
            Position = new Vector3(BitConverter.ToSingle(b, 28), BitConverter.ToSingle(b, 32), BitConverter.ToSingle(b, 36)),
        }).ToList();
        var rest = AnimExportCli.Fbx.SkeletonPose.Rest(decodedBones);
        float db = r.Bones.Select((b, i) => (b.Position - rest.BoneToModel[i].Translation).Length()).Max();
        log($"check:   {count} corners: position off by {dp:0.000000}, UV by {du:0.0000} (half floats), weight by {dw:0.0000} (bytes), {boneBad} influences lost; bones rebuilt from the written data off by {db:0.00000}");
        log($"check:   bounds {string.Join(" ", Enumerable.Range(0, 7).Select(i => BitConverter.ToSingle(back.Bounds, i * 4).ToString("0.#")))} (stock {string.Join(" ", Enumerable.Range(0, 7).Select(i => BitConverter.ToSingle(baseN.Bounds, i * 4).ToString("0.#")))})");
        RenderCheck(back, decodedBones, p2, u2, w2);
    }

    /// <summary>A prop build: the game's prop and the new one from the front, side and back, at rest (props have no
    /// animations of their own: the character's attachment moves them).</summary>
    void PropCheck()
    {
        static Vector3 M(Vector3 v) => new(v.X, -v.Y, v.Z);
        var lod = sk.Mesh.HighestDetail;
        if (lod == null) return;
        var game = new List<RMesh> { new(lod.Positions.Select(M).ToArray(), lod.Indices.ToArray(), lod.TexCoords.ToArray(), null) };
        var made = r.Sections.Select(x => new RMesh(x.Pos.Select(M).ToArray(), x.Tris, x.Uv, x.Tex.Diffuse)).ToList();
        string png = Path.Combine(outDir, sk.Name + ".prop_check.png");
        Snapshot.Sheet(png,
        [
            new("Game's Prop", "front", game, null, "front"), new("New Prop", "front", made, null, "front"),
            new("Game's Prop", "side", game, null, "left"), new("New Prop", "side", made, null, "left"),
            new("Game's Prop", "back", game, null, "back"), new("New Prop", "back", made, null, "back"),
        ]);
        log($"render:  {Path.GetFullPath(png)}");
    }

    void RenderCheck(SkelNative back, List<AnimExportCli.Meshes.MeshBone> decodedBones, Vector3[] p2, Vector2[] u2, (int, float)[][] w2)
    {
        if (PropFit.IsProp(sk)) { PropCheck(); return; }
        var anims = MhoAnim.For(package, sk.Bones.Select(b => b.Name));
        var ar = anims.FirstOrDefault(a => a.Name.Contains(o.CheckAnimation ?? "attack", StringComparison.OrdinalIgnoreCase)) ?? anims.FirstOrDefault();
        var anim = ar != null ? MhoAnim.Load(ar) : null;
        if (anim == null) return;
        float f = MhoAnim.Frames(anim) * 0.5f;
        static Vector3 M(Vector3 v) => new(v.X, -v.Y, v.Z);
        var myBones = MhoAnim.FromGlobals(r.Bones.Select(b => (b.Name, b.Parent, b.Global)).ToList());
        var before = new List<RMesh>();
        foreach (var x in r.Sections)
        {
            var pp = MhoAnim.Pose(myBones, x.Pos, x.Weights, anim, f, ar!.TranslationBones).Select(M).ToArray();
            before.Add(new RMesh(pp, x.Tris, x.Uv, x.Tex.Diffuse));
        }
        var posed = MhoAnim.Pose(decodedBones, p2, w2, anim, f, ar!.TranslationBones).Select(M).ToArray();
        var after = new List<RMesh>();
        foreach (var sec in back.Lods[0].Sections)
        {
            var tris = back.Lods[0].Indices.Skip((int)sec.BaseIndex).Take((int)sec.Triangles * 3).ToArray();
            after.Add(new RMesh(posed, tris, u2, r.Sections.First(x => x.Material == mats[sec.Material]).Tex.Diffuse, FlipV: false));
        }
        string png = Path.Combine(outDir, sk.Name + ".encode_check.png");
        Snapshot.Sheet(png,
        [
            new("Retarget", $"{ar.Name} · frame {f:0}", before, null, "front"),
            new("From Encoded Bytes", "decoded, skinned", after, null, "front"),
            new("Retarget Side", "", before, null, "left"),
            new("Encoded Side", "", after, null, "left"),
        ]);
        log($"render:  {Path.GetFullPath(png)}");
    }
}
