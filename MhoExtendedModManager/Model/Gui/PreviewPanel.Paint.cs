using System.Numerics;
using AnimExportCli.Meshes;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Weight painting (2026-10-25, Kurt: Blender's weight painting, smoothing and inverting, across the model's parts). The bone
/// painted is the Bone Map tab's pick (what Weights shows). Brushes as Blender's: Draw pulls the weight toward the Weight
/// value, Add / Subtract change it by it, Blur pulls it toward its neighbours'; Shift held = Blur, Ctrl held = the other way
/// (Draw toward 1 − Weight, Add ⇄ Subtract). Strength scales each dab; the falloff is smooth over the brush. The brush is a
/// sphere around the point under the pointer as large as the circle there, so it reaches every part it touches (a jacket
/// over a shirt) and seams (vertices at one place) paint as one. Weights stay normalized: the bone's change is taken from or
/// given to the vertex's other bones in their proportions (a vertex on this bone alone gives the rest to its parent), at
/// most 4 bones. Mirror paints the same on the other side (g_l_ ⇄ g_r_). The model's own weights change at once (the
/// animation shows them); the Model tab saves the painted vertices (WeightPaint) when a stroke ends.
/// </summary>
sealed partial class PreviewPanel
{
    public enum Brush { Draw, Add, Subtract, Blur }

    bool painting;
    /// <summary>The view paints (Weights on, the left button paints).</summary>
    public bool Painting
    {
        get => painting;
        set
        {
            if (painting == value) return;
            painting = value;
            view.PaintMode = value;
            if (value && !showWeights) CycleWeights();   // (the weights over the model at half strength first; W cycles)
            else if (showWeights) p3.RefreshSkin();   // (Weights: the painted bone alone while painting, the row's bones otherwise)
            UpdateBrushColor();
            PaintModeChanged?.Invoke();
        }
    }
    public event Action? PaintModeChanged;
    string? paintBone;
    /// <summary>The bone painted (null: nothing to paint); while painting, Weights shows it alone.</summary>
    public string? PaintBone
    {
        get => paintBone;
        set { if (string.Equals(paintBone, value, StringComparison.OrdinalIgnoreCase)) return; paintBone = value; if (painting && showWeights) p3.RefreshSkin(); }
    }
    Brush brush = Brush.Draw;
    public Brush BrushKind { get => brush; set { brush = value; UpdateBrushColor(); } }
    public float PaintWeight { get; set; } = 1;
    public float BrushStrength { get => view.BrushStrength; set => view.BrushStrength = value; }
    public float BrushRadius { get => view.BrushRadius; set { view.BrushRadius = value; view.Controls[0].Invalidate(); } }
    public bool PaintMirror { get; set; }
    /// <summary>F / Shift+F changed the brush in the view.</summary>
    public event Action? BrushChanged;
    /// <summary>A stroke began (before its first change): the Model tab keeps the paint for Undo.</summary>
    public event Action? StrokeStarted;
    /// <summary>A stroke or a whole-bone change ended: the vertices changed (indices in the shown model).</summary>
    public event Action<IReadOnlyCollection<int>>? Painted;
    /// <summary>A line for the log (what couldn't be painted).</summary>
    public event Action<string>? PaintNote;

    readonly HashSet<int> strokeTouched = [];
    bool paintWired;

    void WirePaint()
    {
        if (paintWired) return;
        paintWired = true;
        view.PaintAt += Dab;
        view.PaintEnded += () => { if (strokeTouched.Count > 0) Painted?.Invoke(strokeTouched.ToList()); strokeTouched.Clear(); };
        view.BrushChanged += () => BrushChanged?.Invoke();
    }

    void UpdateBrushColor() => view.BrushColor = brush switch
    {
        Brush.Blur => Color.FromArgb(120, 190, 255),
        Brush.Subtract => Color.FromArgb(255, 120, 120),
        _ => Color.White,
    };

    /// <summary>The bone above (its parent) or below (its first child carrying skin, else its first child) <paramref name="bone"/>,
    /// past helper bones that carry no skin (…_offset); null at the top or bottom.</summary>
    public string? RelativeBone(string bone, bool up)
    {
        if (frameBones is not { } bones) return null;
        int i = -1;
        for (int k = 0; k < bones.Count; k++) if (bones[k].Name.Equals(bone, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        if (i < 0) return null;
        bool Skin(int k) => k >= 0 && k < frameDrawn.Length && frameDrawn[k] && !bones[k].Name.EndsWith("_offset", StringComparison.OrdinalIgnoreCase);
        if (up)
        {
            for (int p = bones[i].ParentIndex, g = 0; p >= 0 && p < bones.Count && p != i && g < 256; p = bones[p].ParentIndex == p ? -1 : bones[p].ParentIndex, g++)
                if (Skin(p)) return bones[p].Name;
            return null;
        }
        // down: children first, then their children (past helpers), the first that carries skin
        var queue = new Queue<int>(Enumerable.Range(0, bones.Count).Where(k => k != i && bones[k].ParentIndex == i));
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            if (Skin(c)) return bones[c].Name;
            foreach (int k in Enumerable.Range(0, bones.Count).Where(k => k != c && bones[k].ParentIndex == c)) queue.Enqueue(k);
        }
        return null;
    }

    /// <summary>F / Shift+F: the radius or strength follows the mouse (the Model tab's keys).</summary>
    public bool BeginBrushAdjust(bool strength) { if (!painting) return false; view.BeginBrushAdjust(strength); return true; }
    public bool CancelBrushAdjust() => painting && view.CancelAdjust();

    // --- the mesh being painted: its welded places, their neighbours, the mirror vertex ----------------------------------------
    ModMeshes.Loaded? paintMesh;
    int[] weld = [];
    int[][] weldMembers = [];
    int[][] weldNeighbours = [];
    int[] mirror = [];

    bool PaintReady(out ModMeshes.Loaded mesh)
    {
        mesh = null!;
        if (shown == null || comparing || shown.TargetOnly) return false;
        mesh = shown.Mesh;
        if (!ReferenceEquals(paintMesh, mesh)) BuildPaintTopology(mesh);
        return true;
    }

    void BuildPaintTopology(ModMeshes.Loaded m)
    {
        paintMesh = m;
        int n = m.Positions.Length;
        // places: vertices within 0.001 units are one (UV seams, material borders, parts that share an edge)
        var key = new Dictionary<(long, long, long), int>();
        weld = new int[n];
        var members = new List<List<int>>();
        for (int v = 0; v < n; v++)
        {
            var p = m.Positions[v] * 1000f;
            var k = ((long)MathF.Round(p.X), (long)MathF.Round(p.Y), (long)MathF.Round(p.Z));
            if (!key.TryGetValue(k, out int g)) { key[k] = g = members.Count; members.Add([]); }
            weld[v] = g; members[g].Add(v);
        }
        weldMembers = [.. members.Select(x => x.ToArray())];
        var nb = new HashSet<int>[members.Count];
        for (int g = 0; g < nb.Length; g++) nb[g] = [];
        for (int t = 0; t + 2 < m.Indices.Length; t += 3)
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                    if (a != b) { int ga = weld[m.Indices[t + a]], gb = weld[m.Indices[t + b]]; if (ga != gb) nb[ga].Add(gb); }
        weldNeighbours = [.. nb.Select(x => x.ToArray())];
        // the mirror vertex: at (x, -y, z) in the bind pose, within 0.05 units
        mirror = new int[n];
        var grid = new Dictionary<(long, long, long), List<int>>();
        const float cell = 0.1f;
        (long, long, long) Cell(Vector3 p) => ((long)MathF.Floor(p.X / cell), (long)MathF.Floor(p.Y / cell), (long)MathF.Floor(p.Z / cell));
        for (int v = 0; v < n; v++) { var c = Cell(m.Positions[v]); if (!grid.TryGetValue(c, out var l)) grid[c] = l = []; l.Add(v); }
        for (int v = 0; v < n; v++)
        {
            var want = m.Positions[v] with { Y = -m.Positions[v].Y };
            var (cx, cy, cz) = Cell(want);
            int best = -1; float bd = 0.05f * 0.05f;
            for (long x = cx - 1; x <= cx + 1; x++) for (long y = cy - 1; y <= cy + 1; y++) for (long z = cz - 1; z <= cz + 1; z++)
                if (grid.TryGetValue((x, y, z), out var l)) foreach (int o in l) { float d = Vector3.DistanceSquared(m.Positions[o], want); if (d < bd) { bd = d; best = o; } }
            mirror[v] = best;
        }
    }

    int BoneIndex(ModMeshes.Loaded m, string name)
    {
        for (int i = 0; i < m.Bones.Count; i++) if (m.Bones[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    static string MirrorName(string bone) =>
        bone.StartsWith("g_l_", StringComparison.OrdinalIgnoreCase) ? "g_r_" + bone[4..]
        : bone.StartsWith("g_r_", StringComparison.OrdinalIgnoreCase) ? "g_l_" + bone[4..]
        : bone;

    static float WeightOf(VertexInfluence inf, int bone)
    {
        float w = 0;
        for (int k = 0; k < inf.Bones.Count; k++) if (inf.Bones[k] == bone) w += inf.Weights[k];
        return w;
    }

    /// <summary>A vertex's weights with <paramref name="bone"/> at <paramref name="value"/> and the others scaled to fill the rest
    /// (a vertex on that bone alone gives the rest to its parent), at most 4 bones.</summary>
    static VertexInfluence SetWeight(VertexInfluence inf, int bone, float value, int fallback)
    {
        value = Math.Clamp(value, 0, 1);
        var others = new Dictionary<int, float>();
        for (int k = 0; k < inf.Bones.Count; k++) if (inf.Bones[k] != bone && inf.Weights[k] > 0) others[inf.Bones[k]] = others.GetValueOrDefault(inf.Bones[k]) + inf.Weights[k];
        float rest = 1 - value, have = others.Values.Sum();
        var acc = new Dictionary<int, float>();
        if (value > 0) acc[bone] = value;
        if (rest > 1e-6f)
        {
            if (have > 1e-6f) foreach (var (b, w) in others.OrderByDescending(x => x.Value).Take(value > 0 ? 3 : 4)) acc[b] = w;
            else if (fallback >= 0 && fallback != bone) acc[fallback] = rest;
            else acc[bone] = 1;   // (nothing else to give it to: the vertex stays on the bone)
        }
        // scale the others to fill what the bone leaves
        float othersSum = acc.Where(x => x.Key != bone).Sum(x => x.Value);
        var list = acc.Select(x => (Bone: x.Key, W: x.Key == bone ? x.Value : othersSum > 1e-6f ? x.Value / othersSum * rest : 0)).Where(x => x.W > 1e-5f).OrderByDescending(x => x.W).ToList();
        float s = list.Sum(x => x.W);
        return new VertexInfluence { Bones = list.Select(x => x.Bone).ToArray(), Weights = list.Select(x => x.W / s).ToArray() };
    }

    static float Falloff(float d, float r) { float t = Math.Clamp(1 - d / Math.Max(1e-6f, r), 0, 1); return t * t * (3 - 2 * t); }

    void Dab(Point at, Keys keys, bool first)
    {
        if (!PaintReady(out var m)) { if (first) PaintNote?.Invoke(comparing ? "Painting is off while Compare shows the target's own model." : "Nothing to paint: pick a source model."); return; }
        if (PaintBone == null || BoneIndex(m, PaintBone) is var bone && bone < 0) { if (first) PaintNote?.Invoke("Pick a bone to paint: a row in the Bone Map tab, or Ctrl+click a bone in the view."); return; }
        if (view.SurfaceAt(at) is not var (hit, upp)) return;
        if (first) StrokeStarted?.Invoke();
        float radius = view.BrushRadius * upp;
        var near = view.VerticesNear(hit, radius, m.Positions.Length);
        if (near.Count == 0) return;
        var kind = Shortcuts.Held("paint.blur", keys) ? Brush.Blur : brush;
        bool ctrl = Shortcuts.Held("paint.otherway", keys);
        if (ctrl && kind == Brush.Add) kind = Brush.Subtract; else if (ctrl && kind == Brush.Subtract) kind = Brush.Add;
        float target = ctrl && kind == Brush.Draw ? 1 - PaintWeight : PaintWeight;
        Apply(m, bone, near.Select(x => (x.Vertex, Falloff(x.Distance, radius))).ToList(), kind, target);
        if (PaintMirror && BoneIndex(m, MirrorName(PaintBone)) is int mb && mb >= 0)
            Apply(m, mb, near.Where(x => mirror[x.Vertex] >= 0).Select(x => (mirror[x.Vertex], Falloff(x.Distance, radius))).ToList(), kind, target);
        p3.RefreshSkin();
    }

    /// <summary>One dab of <paramref name="kind"/> on <paramref name="bone"/> over the vertices with their falloff.</summary>
    void Apply(ModMeshes.Loaded m, int bone, List<(int Vertex, float Falloff)> where, Brush kind, float target)
    {
        if (m.Influences is not List<VertexInfluence> infl) return;
        int fallback = m.Bones[bone].ParentIndex is int pb && pb >= 0 && pb != bone ? pb : -1;
        float s = BrushStrength;
        // Blur: each place toward its neighbours' average, all read before any changes
        Dictionary<int, float>? avg = null;
        if (kind == Brush.Blur)
        {
            avg = [];
            foreach (var (v, _) in where)
            {
                int g = weld[v];
                if (avg.ContainsKey(g) || weldNeighbours[g].Length == 0) continue;
                float sum = 0;
                foreach (int o in weldNeighbours[g]) sum += WeightOf(infl[weldMembers[o][0]], bone);
                avg[g] = sum / weldNeighbours[g].Length;
            }
        }
        foreach (var (v, f) in where)
        {
            if (f <= 0) continue;
            float w = WeightOf(infl[v], bone);
            float nw = kind switch
            {
                Brush.Draw => w + (target - w) * s * f,
                Brush.Add => w + target * s * f,
                Brush.Subtract => w - target * s * f,
                _ => avg != null && avg.TryGetValue(weld[v], out float a) ? w + (a - w) * s * f : w,
            };
            if (MathF.Abs(nw - w) < 1e-5f) continue;
            // every vertex at this place the same (a seam paints as one)
            foreach (int o in weldMembers[weld[v]]) { infl[o] = SetWeight(infl[o], bone, nw, fallback); strokeTouched.Add(o); }
        }
    }

    /// <summary>Invert (w → 1 − w on the vertices the bone moves) or Smooth (5 × 0.5 over the whole model, as Blender's Weights
    /// → Smooth) the painted bone; Mirror does the other side too.</summary>
    public void WholeBone(bool invert)
    {
        if (!PaintReady(out var m) || m.Influences is not List<VertexInfluence> infl) { PaintNote?.Invoke("Nothing to paint: pick a source model."); return; }
        if (PaintBone == null || BoneIndex(m, PaintBone) is var bone && bone < 0) { PaintNote?.Invoke("Pick a bone first: a row in the Bone Map tab, or Ctrl+click a bone in the view."); return; }
        StrokeStarted?.Invoke();
        var bones = new List<int> { bone };
        if (PaintMirror && BoneIndex(m, MirrorName(PaintBone)) is int mb && mb >= 0 && mb != bone) bones.Add(mb);
        foreach (int b in bones)
        {
            int fallback = m.Bones[b].ParentIndex is int pb && pb >= 0 && pb != b ? pb : -1;
            if (invert)
            {
                for (int v = 0; v < infl.Count; v++)
                {
                    float w = WeightOf(infl[v], b);
                    if (w <= 1e-5f) continue;
                    infl[v] = SetWeight(infl[v], b, 1 - w, fallback); strokeTouched.Add(v);
                }
            }
            else
                for (int step = 0; step < 5; step++)
                {
                    var place = new float[weldMembers.Length];
                    for (int g = 0; g < place.Length; g++) place[g] = WeightOf(infl[weldMembers[g][0]], b);
                    for (int g = 0; g < place.Length; g++)
                    {
                        if (weldNeighbours[g].Length == 0) continue;
                        float a = 0; foreach (int o in weldNeighbours[g]) a += place[o];
                        a /= weldNeighbours[g].Length;
                        float nw = place[g] + (a - place[g]) * 0.5f;
                        if (MathF.Abs(nw - place[g]) < 1e-4f) continue;
                        foreach (int v in weldMembers[g]) { infl[v] = SetWeight(infl[v], b, nw, fallback); strokeTouched.Add(v); }
                    }
                }
        }
        p3.RefreshSkin();
        if (strokeTouched.Count > 0) Painted?.Invoke(strokeTouched.ToList());
        strokeTouched.Clear();
    }

    /// <summary>The shown model's vertex <paramref name="v"/>: its section (in the Prepared lists) and index there, and its
    /// weights by bone name.</summary>
    public (int Section, int Local, Dictionary<string, float> Weights)? PaintedVertex(int v)
    {
        if (shown == null || v < 0) return null;
        int start = 0;
        for (int si = 0; si < shown.SectionCounts.Count; si++)
        {
            int c = shown.SectionCounts[si];
            if (v < start + c)
            {
                var inf = shown.Mesh.Influences[v];
                var d = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                for (int k = 0; k < inf.Bones.Count; k++)
                    if (inf.Bones[k] >= 0 && inf.Bones[k] < shown.Bones.Count && inf.Weights[k] > 1e-5f) d[shown.Bones[inf.Bones[k]].Name] = MathF.Round(inf.Weights[k], 5);
                return (si, v - start, d);
            }
            start += c;
        }
        return null;
    }

    /// <summary>The shown model's sections as the paint file keys them.</summary>
    public List<(string Part, string Material, int Count, int Nth)> PaintKeys() =>
        shown == null ? [] : WeightPaint.Keys(shown.SectionParts.Zip(shown.SectionNames, shown.SectionCounts).Select(x => (x.First, x.Second, x.Third)));

    /// <summary>Test: one stroke of one dab on <paramref name="bone"/> at the point of the model nearest the view's centre
    /// (a spiral search from the centre); returns the vertex nearest the dab and the vertices changed, or null when no point of
    /// the model is found.</summary>
    internal (int Center, List<int> Touched)? TestStroke(string bone, Keys keys)
    {
        PaintBone = bone;
        var sz = view.Controls[0].ClientSize;
        var c = new Point(sz.Width / 2, sz.Height / 2);
        Point? at = null;
        for (int r = 0; r < Math.Min(sz.Width, sz.Height) / 2 && at == null; r += 4)
            for (int a = 0; a < 16 && at == null; a++)
            {
                var p = new Point(c.X + (int)(r * MathF.Cos(a * MathF.PI / 8)), c.Y + (int)(r * MathF.Sin(a * MathF.PI / 8)));
                if (view.SurfaceAt(p) is not null) at = p;
            }
        if (at is not Point hitAt || view.SurfaceAt(hitAt) is not var (hit, _)) return null;
        int center = view.VerticesNear(hit, float.MaxValue, shown?.Mesh.Positions.Length ?? 0).OrderBy(x => x.Distance).First().Vertex;
        Dab(hitAt, keys, true);
        var touched = strokeTouched.ToList();
        if (strokeTouched.Count > 0) Painted?.Invoke(touched);
        strokeTouched.Clear();
        return (center, touched);
    }

    /// <summary>Test: a vertex's weight on a bone in the shown model.</summary>
    internal float TestWeight(int v, string bone) => shown == null || BoneIndex(shown.Mesh, bone) is var b && b < 0 ? -1 : WeightOf(shown.Mesh.Influences[v], b);
}
