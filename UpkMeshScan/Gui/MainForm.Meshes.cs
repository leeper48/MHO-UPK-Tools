using System.Numerics;

namespace UpkMeshScan.Gui;

/// <summary>
/// The Meshes tab's 3D view: the mesh selected in the list, with each section's diffuse texture when the package has it
/// (the section's material instance, followed to its parents, then the texture's largest mip from the package or the
/// .tfc caches). Loaded in the background; the newest selection wins.
/// </summary>
sealed partial class MainForm
{
    readonly MeshViewer meshViewer = new() { Dock = DockStyle.Fill };
    int meshRequest;

    void PreviewMesh(MeshItem m)
    {
        if (package == null) return;
        var pkg = package;
        int request = ++meshRequest;
        string folder = gameFolder.Text;
        meshViewer.ShowMessage($"Loading {m.Name}...");
        Task.Run<(StaticMesh? Mesh, MeshViewer.Tex?[] Tex, string Info)>(() =>
        {
            StaticMesh sm;
            try { sm = StaticMesh.Read(pkg, pkg.Exports[m.Index]); }
            catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { return (null, [], $"can't be read: {ex.Message}"); }
            var tex = new MeshViewer.Tex?[sm.Sections.Length];
            var notes = new List<string>();
            var cache = new Dictionary<int, MeshViewer.Tex?>();
            for (int s = 0; s < sm.Sections.Length; s++)
            {
                int mat = sm.Sections[s].MaterialRef;
                if (mat == 0) { notes.Add($"section {s}: no material (the placed component supplies it)"); continue; }
                var list = TextureExport.MaterialTextures(pkg, mat, new List<string>());
                // A base Material (or an instance without texture parameters of its own): the textures its compiled shader uses.
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
                if (pick == null) { notes.Add($"section {s}: no colour texture in {pkg.RefName(mat)}{(mat < 0 ? " (a material from another package)" : "")}"); continue; }
                if (!cache.TryGetValue(pick.ExportIndex, out var t))
                {
                    t = null;
                    try
                    {
                        if (TextureExport.ReadBestMip(pkg, pick.ExportIndex, out string note, folder) is { } mip && TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is byte[] px)
                            t = new MeshViewer.Tex(px, mip.Width, mip.Height);
                        else notes.Add($"section {s}: {pick.Texture} can't be shown ({note})");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException) { notes.Add($"section {s}: {pick.Texture}: {ex.Message}"); }
                    cache[pick.ExportIndex] = t;
                }
                tex[s] = t;
            }
            int shown = tex.Count(t => t != null);
            return (sm, tex, $"textures: {shown} of {tex.Length} section(s)" + (notes.Count > 0 ? "; " + string.Join("; ", notes.Distinct().Take(3)) : ""));
        }).ContinueWith(task =>
        {
            if (request != meshRequest) return;                            // another mesh was selected meanwhile
            var (sm, tex, info) = task.Result;
            if (sm == null) { meshViewer.ShowMessage($"{m.Name}: {info}"); return; }
            Log($"Mesh {m.Name}: {info}");
            var tri = new int[sm.Indices.Length / 3];
            for (int s = 0; s < sm.Sections.Length; s++)
            {
                var sec = sm.Sections[s];
                for (int k = 0; k < sec.NumTriangles; k++) if (sec.FirstIndex / 3 + k < tri.Length) tri[sec.FirstIndex / 3 + k] = s;
            }
            meshViewer.ShowMesh(m.Name, sm.Positions, sm.Normals, sm.TexCoords.Length > 0 ? sm.TexCoords[0] : [], [.. sm.Indices.Select(i => (int)i)], tri, tex, info);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
