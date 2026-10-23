using System.Numerics;
using MhoPackageModifier;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The 3D views' size reference (Kurt, 2026-10-23: a 55-gallon oil drum from the game, Look ▾ → Size Reference): the
/// Brooklyn levels' mtnt_undrgrnd_oildrum_a static mesh, read once from the game's stock copy, standing on the floor beside
/// the character (not on a bone: it stays put while the character moves). Read only.
/// </summary>
static class SizeReference
{
    /// <summary>Packages that carry the drum (the first one found is read).</summary>
    static readonly string[] Packages = ["Brooklyn_Fishery_A.upk", "Brooklyn_Rhino_Boss_A.upk", "Brooklyn_ShipyardCrane_A.upk"];
    const string MeshPath = "mtnt_undrgrnd_oildrum_a.meshes.mtnt_undrgrnd_oildrum_a";
    /// <summary>The drum's material: its mesh sections name none (the level's placed component sets it).</summary>
    const string MaterialPath = "mtnt_undrgrnd_oildrum_a.materials.mtnt_undgrnd_oildruml_mat_a";
    static (string Cooked, ModMeshes.Loaded Mesh, Matrix4x4 Base, Vector2 Beside)? cached;

    /// <summary>Where the drum stands now (engine X, Y of its middle): dragged in a 3D view (saved on the drop), else beside
    /// the character.</summary>
    public static Vector2 Spot
    {
        get => spot ?? (PreviewViews.SizeRefAt is [var x, var y] ? new Vector2(x, y) : cached?.Beside ?? Vector2.Zero);
        set => spot = value;
    }
    static Vector2? spot;
    /// <summary>The spot dragged to, saved for every 3D view.</summary>
    public static void Keep() { if (spot is { } s) PreviewViews.SizeRefAt = [s.X, s.Y]; }
    /// <summary>Back beside the character (a double-click on the drum).</summary>
    public static void Reset() { spot = null; PreviewViews.SizeRefAt = null; }
    /// <summary>The drum's placement at <see cref="Spot"/>.</summary>
    public static Matrix4x4 Place => cached is { } c ? c.Base * Matrix4x4.CreateTranslation(Spot.X, Spot.Y, 0) : Matrix4x4.Identity;
    static readonly object gate = new();

    /// <summary>The drum and where it stands (beside the character, on the floor), or null when it can't be read.</summary>
    public static (ModMeshes.Loaded Mesh, Matrix4x4 Place)? For(string? cooked)
    {
        if (cooked == null) return null;
        lock (gate)
        {
            if (cached is { } c && c.Cooked == cooked) return (c.Mesh, Place);
            foreach (string f in Packages)
            {
                string path = StockFiles.For(cooked, f);
                if (!File.Exists(path)) continue;
                try
                {
                    var pkg = Package.Open(path);
                    int e = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(MeshPath, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase));
                    if (e < 0) continue;
                    var sm = StaticMesh.Read(pkg, pkg.Exports[e]);
                    var pos = sm.Positions;
                    var nrm = sm.Normals.Length == pos.Length ? sm.Normals : new Vector3[pos.Length];
                    var uv = sm.TexCoords.Length > 0 && sm.TexCoords[0].Length == pos.Length ? sm.TexCoords[0] : new Vector2[pos.Length];
                    int[] idx = [.. sm.Indices.Select(i => (int)i)];
                    var tri = new int[idx.Length / 3];
                    var looks = new ModelView.Look?[sm.Sections.Length];
                    int mat = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(MaterialPath, StringComparison.OrdinalIgnoreCase));
                    for (int s = 0; s < sm.Sections.Length; s++)
                    {
                        var sec = sm.Sections[s];
                        for (int t = 0; t < sec.NumTriangles && sec.FirstIndex / 3 + t < tri.Length; t++) tri[sec.FirstIndex / 3 + t] = s;
                        looks[s] = sec.MaterialRef > 0 ? ModMeshes.LookOf(path, sec.MaterialRef - 1, cooked) : mat >= 0 ? ModMeshes.LookOf(path, mat, cooked) : null;
                    }
                    var mesh = new ModMeshes.Loaded("size reference: oil drum", pos, nrm, ModMeshes.Tangents(pos, nrm, uv, idx), uv, idx, tri, looks,
                        $"{pos.Length} vertices", [], []);
                    // on the floor, centred under itself; at first to the character's side (−Y: away from the default camera,
                    // which looks from +X turned toward +Y) and a little behind
                    float minZ = pos.Min(v => v.Z), cx = (pos.Min(v => v.X) + pos.Max(v => v.X)) / 2, cy = (pos.Min(v => v.Y) + pos.Max(v => v.Y)) / 2;
                    float half = Math.Max(pos.Max(v => v.Y) - pos.Min(v => v.Y), pos.Max(v => v.X) - pos.Min(v => v.X)) / 2;
                    cached = (cooked, mesh, Matrix4x4.CreateTranslation(-cx, -cy, -minZ), new Vector2(-20, -(70 + half)));
                    return (mesh, Place);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
            }
            return null;
        }
    }
}
