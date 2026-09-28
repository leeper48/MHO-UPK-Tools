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
/// package or the game's .tfc caches). The 3D view itself is MPM's MeshViewer (software renderer).
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

    public sealed record Loaded(string Name, Vector3[] Positions, Vector3[] Normals, Vector2[] Uv, int[] Indices, int[] TriangleSection, MeshViewer.Tex?[] Textures, string Info,
        IReadOnlyList<MeshBone> Bones, IReadOnlyList<VertexInfluence> Influences);

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
        var materials = new List<int>();
        if (pkg.TryReadProperties(r.Export) is { } props)
        {
            var d = pkg.GetExportData(r.Export);
            int at = props.PayloadOffset + MeshBounds.ByteSize;
            if (at + 4 <= d.Length)
            {
                int n = BitConverter.ToInt32(d.Slice(at, 4));
                for (int i = 0; i < n && i < 256 && at + 8 + 4 * i <= d.Length; i++) materials.Add(BitConverter.ToInt32(d.Slice(at + 4 + 4 * i, 4)));
            }
        }
        var tex = new MeshViewer.Tex?[lod.Sections.Count];
        var notes = new List<string>();
        Package? mpm = null;
        try { mpm = Package.Open(r.File); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { notes.Add("textures: " + ex.Message); }
        var cache = new Dictionary<int, MeshViewer.Tex?>();
        for (int s = 0; s < lod.Sections.Count && mpm != null; s++)
        {
            int mi = lod.Sections[s].MaterialIndex;
            int mat = mi >= 0 && mi < materials.Count ? materials[mi] : 0;
            if (mat == 0) { notes.Add($"section {s}: no material"); continue; }
            tex[s] = SectionTexture(mpm, mat, cacheFolder, cache, notes, s);
        }
        var tri = new int[lod.Indices.Count / 3];
        for (int s = 0; s < lod.Sections.Count; s++)
        {
            var sec = lod.Sections[s];
            for (int k = 0; k < sec.TriangleCount; k++) if (sec.BaseIndex / 3 + k < tri.Length) tri[sec.BaseIndex / 3 + k] = s;
        }
        int shown = tex.Count(t => t != null);
        return new Loaded(r.Name, [.. lod.Positions], [.. lod.Normals], [.. lod.TexCoords], [.. lod.Indices], tri, tex,
            $"{lod.Positions.Count:N0} vertices, {lod.TriangleCount:N0} triangles, textures {shown} of {tex.Length}" + (notes.Count > 0 ? "; " + string.Join("; ", notes.Distinct().Take(3)) : ""),
            mesh.Bones, lod.Influences);
    }

    /// <summary>A section's colour texture (as MPM's Meshes tab chooses it).</summary>
    static MeshViewer.Tex? SectionTexture(Package pkg, int mat, string? cacheFolder, Dictionary<int, MeshViewer.Tex?> cache, List<string> notes, int s)
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
        if (pick == null) { notes.Add($"section {s}: no colour texture{(mat < 0 ? " (material from another package)" : "")}"); return null; }
        if (cache.TryGetValue(pick.ExportIndex, out var t)) return t;
        t = null;
        try
        {
            if (TextureExport.ReadBestMip(pkg, pick.ExportIndex, out string note, cacheFolder) is { } mip && TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is byte[] px)
                t = new MeshViewer.Tex(px, mip.Width, mip.Height);
            else notes.Add($"section {s}: {pick.Texture} can't be shown ({note})");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException) { notes.Add($"section {s}: {pick.Texture}: {ex.Message}"); }
        cache[pick.ExportIndex] = t;
        return t;
    }
}
