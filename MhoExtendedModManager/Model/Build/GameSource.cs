using AnimExportCli.Meshes;
using AnimPackage = AnimExportCli.Packages.Package;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// From the Game as a source (Kurt, 2026-10-23: pick player costumes, team-ups and NPCs as the source, like the target): the
/// package's character (the costume's own model, else its hero's base package's) written once as an FBX with MHO bones, in the
/// layout Export FBX writes (model.fbx, &lt;material&gt;.png / _n / _mhospec / _speccolor / _glow beside it), so it's read as any
/// MHO-boned FBX source. Read from the game's stock copy; kept in data\model\game\&lt;package&gt; until that copy changes.
/// </summary>
static class GameSource
{
    public static string Folder => Path.Combine(Settings.Home, "game");

    /// <summary>The FBX for <paramref name="packageFile"/> (a game package name), written if it isn't there yet or is stale.</summary>
    /// <param name="meshName">A model of the package by name (a prop: Kurt, 2026-10-26, props in the Source's panel), kept apart from
    /// the package's character in its own folder; null: the character the Model tab picks for the package.</param>
    /// <summary>Current Modded Game (Kurt, 2026-10-28): a package's path as the mod list has it (an enabled mod's copy),
    /// or null for the game's own. Set by the Model tab while that source is picked.</summary>
    public static Func<string, string?>? Modded { get; set; }

    /// <summary>The package a source reads: the mod list's copy (Current Modded Game), else the game's stock one.</summary>
    public static string PathFor(string packageFile) => Modded?.Invoke(packageFile) ?? BasePackage.Resolve(packageFile, true);

    public static string Export(string packageFile, Action<string> log, string? meshName = null)
    {
        string? modded = Modded?.Invoke(packageFile);
        string stock = modded ?? BasePackage.Resolve(packageFile, true);
        string meshPath = Gui.PreviewPanel.MeshPackage(stock);
        string outDir = Path.Combine(Folder, Path.GetFileNameWithoutExtension(packageFile) + (meshName != null ? "__" + meshName : "") + (modded != null ? "__modded" : ""));
        string fbx = Path.Combine(outDir, "model.fbx"), stampFile = Path.Combine(outDir, "source.txt");
        var fi = new FileInfo(meshPath);
        // the character the Model tab fits to (MhoSkeleton.Load: the chosen one, a summon's or power model's mesh, the costume's
        // own), else the package's first
        string? pickName = null;
        if (meshName != null) pickName = meshName;
        else try { pickName = MhoSkeleton.Load(meshPath).Mesh.Name; } catch (InvalidDataException) { }
        string stamp = $"{meshPath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{pickName}|3";   // (3: no glow from a DXT1 map's missing alpha)
        if (File.Exists(fbx) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return fbx;

        Protected.CheckWrite(outDir);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);   // (our own cache folder: written only here)
        Directory.CreateDirectory(outDir);
        var refs = ModMeshes.List([(Path.GetFileName(meshPath), meshPath)], anyPackage: true);
        if (refs.Count == 0) throw new InvalidDataException($"{packageFile} has no character model.");
        var main = refs.FirstOrDefault(x => x.Name.Equals(pickName, StringComparison.OrdinalIgnoreCase)) ?? refs[0];
        var apkg = AnimPackage.Open(meshPath);
        string why = "";
        var mesh = SkeletalMeshReader.TryRead(apkg, main.Export, e => why = e);
        if (mesh?.HighestDetail is not { HasGeometry: true } lod) throw new InvalidDataException($"{main.Name} in {Path.GetFileName(meshPath)} can't be read ({(why.Length > 0 ? why : "no geometry")}).");

        // each section's material, as the game draws it (the mesh component's own list first)
        var pkg = Package.Open(meshPath);
        var materials = ModMeshes.MaterialRefs(apkg, main.Export);
        if (ModMeshes.ComponentMaterials(pkg, meshPath, main.Name) is { Count: > 0 } over)
        {
            materials = [.. materials];
            for (int k = 0; k < over.Count && k < materials.Count; k++) if (over[k] != 0) materials[k] = over[k];
        }
        string? cooked = Settings.Current.CookedFolder;
        var names = new List<string>();
        var tex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var done = new Dictionary<int, string>();
        int maps = 0;
        for (int s = 0; s < lod.Sections.Count; s++)
        {
            int mi = lod.Sections[s].MaterialIndex, mat = mi >= 0 && mi < materials.Count ? materials[mi] : 0;
            if (done.TryGetValue(mat, out var known)) { names.Add(known); continue; }
            Package mp = pkg; int mref = mat;
            if (mat < 0 && ModMeshes.ImportedMaterialAt(main, pkg, mat, cooked) is { } at) { mp = at.Pkg; mref = at.Index + 1; }
            MaterialInfo? info = null;
            try { info = mref > 0 ? ModMaterials.Read(mp, mref) : null; } catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
            string name = info?.Name ?? (mref > 0 && mref <= mp.Exports.Length ? mp.Exports[mref - 1].ObjectName : $"section{s}");
            while (done.ContainsValue(name)) name += "_" + s;
            done[mat] = name; names.Add(name);
            if (info == null) continue;
            string b = Path.Combine(outDir, FbxExport.SafeName(name));
            foreach (var (param, export) in info.Textures)
            {
                string p = param.ToLowerInvariant();
                if (p.Contains("reflect") && !p.Contains("spec") || p.Contains("detail") || p.Contains("mask") && !p.Contains("spec")) continue;
                if (p.Contains("diffuse")) { if (Save(mp, export, b + ".png", cooked)) { tex[name] = Path.GetFileName(b + ".png"); maps++; } }
                else if (p.Contains("norm")) { if (Save(mp, export, b + "_n.png", cooked)) maps++; }
                else if (p.Contains("speccolor")) { if (Save(mp, export, b + "_speccolor.png", cooked)) maps++; }
                else if (SpecLayouts.FromName(p) is { } layout)
                {
                    // a packed spec map: as the tab reads it (Angela's layout), its glow channel as a glow map
                    string raw = Path.Combine(outDir, $"{FbxExport.SafeName(name)}_{p}.png");
                    if (!Save(mp, export, raw, cooked)) continue;
                    string angela = SpecLayouts.ToAngela(raw, layout);
                    File.Copy(angela, b + "_mhospec.png", true);
                    if (layout.GlowChannel >= 0 && File.Exists(b + ".png") && SpecLayouts.GlowMap(raw, layout, b + ".png") is string glow && File.Exists(glow))
                        File.Copy(glow, b + "_glow.png", true);
                    maps++;
                }
                else if (p.Contains("emissive") || p.Contains("glow")) { if (Save(mp, export, b + "_glow.png", cooked)) maps++; }
            }
        }
        FbxExport.WriteModel(fbx, names, mesh, lod, tex);
        File.WriteAllText(stampFile, stamp);
        log($"{packageFile}: {main.Name} from {Path.GetFileName(meshPath)} written as a source ({lod.Positions.Count:N0} vertices, {lod.Sections.Count} sections, {mesh.Bones.Count} bones, {maps} maps) in {outDir}");
        return fbx;
    }

    /// <summary>A texture's largest mip as a PNG.</summary>
    static bool Save(Package p, int export, string png, string? cooked)
    {
        try
        {
            var mip = TextureExport.ReadBestMip(p, export, out _, cooked);
            if (mip == null || TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is not byte[] px) return false;
            using var bmp = TextureDecode.ToBitmap(px, mip.Width, mip.Height);
            ImagePixels.SavePng(bmp, png);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IOException or IndexOutOfRangeException or ArgumentException) { return false; }
    }
}
