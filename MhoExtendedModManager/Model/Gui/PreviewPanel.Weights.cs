using System.Numerics;
using MhoExtendedModManager;
using MhoExtendedModManager.Gui;

namespace MhoMffImporter.Gui;

/// <summary>
/// Weight paint view (0.13.1, Kurt: "an overlay showing the weight of influence like Blender does with weight painting"):
/// the model coloured by how much the Bone Map tab's selected bones move each vertex, in Blender's colours (blue 0 → cyan →
/// green → yellow → red 1), shaded and posed like the model. Done without touching the vendored view: a copy of the mesh
/// whose texture coordinate is the weight itself (u = weight) over a small gradient texture, so the renderer blends the
/// colour across each triangle exactly as the weights blend (mirrored UVs don't matter: the model's own UVs aren't used).
/// </summary>
sealed partial class PreviewPanel
{
    bool showWeights;
    static ModelView.Look? weightLook;

    /// <summary>Whether the weight paint view is on (Bone Map tab → Weights, Look ▾ → Weights; not remembered: a check tool).</summary>
    public bool ShowWeights
    {
        get => showWeights;
        set { if (showWeights == value) return; showWeights = value; RefreshDisplay(); TogglesChanged?.Invoke(); }
    }

    /// <summary>Bones or Weights changed (the Bone Map tab's buttons follow the Look menu).</summary>
    public event Action? TogglesChanged;

    /// <summary>Blender's weight colours: 0 blue, 0.25 cyan, 0.5 green, 0.75 yellow, 1 red.</summary>
    static Color WeightColor(float w)
    {
        (float At, Color C)[] stops = [(0, Color.FromArgb(0, 0, 255)), (0.25f, Color.FromArgb(0, 255, 255)), (0.5f, Color.FromArgb(0, 255, 0)), (0.75f, Color.FromArgb(255, 255, 0)), (1, Color.FromArgb(255, 0, 0))];
        w = Math.Clamp(w, 0, 1);
        for (int i = 1; i < stops.Length; i++)
            if (w <= stops[i].At)
            {
                float t = (w - stops[i - 1].At) / (stops[i].At - stops[i - 1].At);
                var a = stops[i - 1].C; var b = stops[i].C;
                return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
            }
        return stops[^1].C;
    }

    static ModelView.Look WeightLook()
    {
        if (weightLook != null) return weightLook;
        const int W = 256, H = 4;
        var px = new byte[W * H * 4];
        for (int x = 0; x < W; x++)
        {
            var c = WeightColor(x / (W - 1f));
            for (int y = 0; y < H; y++) { int i = (y * W + x) * 4; px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
        }
        var look = ModelView.Look.Plain(new ModelView.Map(px, W, H));
        look.Cutout = false; look.TwoSided = true; look.HalfLambert = true; look.UseRim = false;
        return weightLook = look;
    }

    /// <summary>The mesh to show: the model itself, or with Weights on its weight-paint copy (same vertices, triangles,
    /// bones and influences, so animation and props work unchanged).</summary>
    ModMeshes.Loaded Display(ModMeshes.Loaded l)
    {
        if (!showWeights) return l;
        var hotIdx = new bool[l.Bones.Count];
        for (int b = 0; b < l.Bones.Count; b++) hotIdx[b] = hot.Contains(l.Bones[b].Name);
        var uv = new Vector2[l.Positions.Length];
        for (int v = 0; v < uv.Length && v < l.Influences.Count; v++)
        {
            float w = 0;
            var inf = l.Influences[v];
            for (int k = 0; k < inf.Bones.Count; k++) if (inf.Bones[k] >= 0 && inf.Bones[k] < hotIdx.Length && hotIdx[inf.Bones[k]]) w += inf.Weights[k];
            // texel centres: 0 → the first texel, 1 → the last (no blending past the ends)
            uv[v] = new Vector2((0.5f + Math.Clamp(w, 0, 1) * 255f) / 256f, 0.5f);
        }
        return new ModMeshes.Loaded(l.Name, l.Positions, l.Normals, l.Tangents, uv, l.Indices, new int[l.Indices.Length / 3],
            [WeightLook()], l.Info, l.Bones, l.Influences);
    }

    /// <summary>Shows the current character again (weights on / off, another bone picked), keeping the camera and the pose.</summary>
    void RefreshDisplay()
    {
        if (shownLoaded == null || animator == null) return;
        var keep = view.ViewState;
        if (rig.Count > 0) RebuildRig();
        else { view.ShowMesh(Display(shownLoaded)); view.ViewState = keep; ShowPose(); }
        view.ViewState = keep;
    }
}
