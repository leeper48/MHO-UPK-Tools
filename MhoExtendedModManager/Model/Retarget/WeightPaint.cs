using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Weight painting (2026-10-25, Kurt: "we are going to want the weight painting feature", with Blender's smoothing and
/// inverting, across the model's parts). The Model tab's brush writes each painted vertex's whole weight list (bone names) to
/// a file per source and target (Model\weights\&lt;source&gt; on &lt;target&gt;.json); it's applied last, after the fit, the
/// Bone Map's smoothing and a mesh edited in Blender, in the preview, Build and the FBX export alike. A vertex is found by its
/// section (part, material, vertex count, and which of those it is) and its index there, so a model with other parts ticked
/// keeps the paint on the parts that stay; a section whose vertex count changed (Smooth (Subdivide), a new Blender mesh)
/// drops its paint, and the note says how many vertices.
/// </summary>
sealed class WeightPaint
{
    public sealed class Section
    {
        public string Part { get; set; } = "";
        public string Material { get; set; } = "";
        public int Count { get; set; }
        /// <summary>Which section with the same part, material and count (0 = the first).</summary>
        public int Nth { get; set; }
        /// <summary>Vertex index → bone name → weight (the whole list, summing to 1).</summary>
        public SortedDictionary<int, Dictionary<string, float>> Vertices { get; set; } = new();
    }

    public List<Section> Sections { get; set; } = new();

    [JsonIgnore] public int VertexCount => Sections.Sum(s => s.Vertices.Count);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>A paint file's text (Undo's copy) read; null or bad text = no paint.</summary>
    public static WeightPaint FromText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new();
        try { return JsonSerializer.Deserialize<WeightPaint>(text, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static WeightPaint Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<WeightPaint>(File.ReadAllText(path), Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string path)
    {
        Sections.RemoveAll(s => s.Vertices.Count == 0);
        if (Sections.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        Protected.CheckWrite(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>The key of each of <paramref name="sections"/>: (part, material, count, nth).</summary>
    public static List<(string Part, string Material, int Count, int Nth)> Keys(IEnumerable<(string Part, string Material, int Count)> sections)
    {
        var seen = new Dictionary<(string, string, int), int>();
        var keys = new List<(string, string, int, int)>();
        foreach (var (p, m, c) in sections)
        {
            int n = seen.GetValueOrDefault((p, m, c));
            seen[(p, m, c)] = n + 1;
            keys.Add((p, m, c, n));
        }
        return keys;
    }

    public Section? Find((string Part, string Material, int Count, int Nth) k) =>
        Sections.FirstOrDefault(s => s.Part == k.Part && s.Material == k.Material && s.Count == k.Count && s.Nth == k.Nth);

    public Section For((string Part, string Material, int Count, int Nth) k)
    {
        var s = Find(k);
        if (s == null) Sections.Add(s = new Section { Part = k.Part, Material = k.Material, Count = k.Count, Nth = k.Nth });
        return s;
    }

    /// <summary>Applies the file's paint to the retarget's weights; null when there's none, else a line for the log.</summary>
    public static string? Apply(Retargeted r, string? path)
    {
        var paint = Load(path);
        if (paint.VertexCount == 0) return null;
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < r.Bones.Count; i++) byName.TryAdd(r.Bones[i].Name, i);
        var keys = Keys(r.Sections.Select(s => (s.Part, s.Material, s.Pos.Length)));
        int done = 0, lost = 0, unknownBones = 0;
        var used = new HashSet<Section>();
        for (int si = 0; si < r.Sections.Count; si++)
        {
            if (paint.Find(keys[si]) is not { } ps) continue;
            used.Add(ps);
            var s = r.Sections[si];
            foreach (var (v, ws) in ps.Vertices)
            {
                if (v < 0 || v >= s.Weights.Length) { lost++; continue; }
                var list = new List<(int Bone, float Weight)>();
                foreach (var (name, w) in ws)
                    if (byName.TryGetValue(name, out int b)) list.Add((b, w)); else unknownBones++;
                float sum = list.Sum(x => x.Weight);
                if (sum <= 1e-6f) { lost++; continue; }
                s.Weights[v] = [.. list.OrderByDescending(x => x.Weight).Take(4).Select(x => (x.Bone, x.Weight / sum))];
                done++;
            }
        }
        lost += paint.Sections.Where(x => !used.Contains(x)).Sum(x => x.Vertices.Count);
        return $"weight paint: {done:N0} painted vertices" + (lost > 0 ? $" ({lost:N0} no longer match the model: its parts or vertices changed)" : "")
            + (unknownBones > 0 ? $" ({unknownBones} weights on bones this skeleton doesn't have were left out)" : "");
    }

    /// <summary>The paint file of a source on a target in a mod's Model folder.</summary>
    public static string PathFor(string workFolder, string source, string targetPackage) =>
        System.IO.Path.Combine(workFolder, "weights", $"{source} on {System.IO.Path.GetFileNameWithoutExtension(targetPackage)}.json");
}
