using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MhoMffImporter;

/// <summary>
/// The editable bone map (Kurt: an editable map for the odd cases). <c>--bone-map</c> writes what the retarget chose; edited
/// and passed back with <c>--map</c>, the file decides the mapping completely (nothing is guessed on top):
/// <list type="bullet">
/// <item><b>bones</b>: one line per MFF bone. <c>mho</c> = the MHO bone it drives (its joint places that bone and its
/// weights go there); <c>null</c> = its weights go to the nearest mapped parent. Lines with <c>how: "chain"</c> are only a
/// report of the chain pairing below and are ignored when read (edit the chains instead).</item>
/// <item><b>chains</b>: MFF extra chain (by its first bone) → MHO chain (by its first bone), or <c>mho: null</c> to leave that
/// MFF chain on its parent. Chains not listed aren't paired.</item>
/// </list>
/// Names are checked when read (a typo names the bone instead of doing nothing).
/// </summary>
sealed class BoneMapFile
{
    public string Mff { get; set; } = "";
    public string Mho { get; set; } = "";
    public string Note { get; set; } =
        "Edit 'mho' in bones (null = weights to the nearest mapped parent) and chains (null = not paired). " +
        "Lines with how 'chain' only report the chain pairing and are ignored when read. Use: --retarget ... --map <this file>";
    public List<BoneEntry> Bones { get; set; } = new();
    public List<ChainEntry> Chains { get; set; } = new();
    /// <summary>Weight smoothing (0.12.0, the Bone Map tab's Smooth Weights): MHO bone → clicks (each <see cref="WeightSmooth.StepsPerPass"/>
    /// steps at 0.5), applied after the retarget in this order. Empty when not used.</summary>
    public List<SmoothEntry> Smooth { get; set; } = new();

    public sealed class SmoothEntry
    {
        public string Bone { get; set; } = "";
        public int Passes { get; set; }
    }

    public sealed class BoneEntry
    {
        public string Mff { get; set; } = "";
        public string? Mho { get; set; }
        public string How { get; set; } = "";
        /// <summary>The bone's own name in the FBX when a skeleton profile renamed it (Mixamo, or guessed from the shape): shown
        /// in the Bone Map instead of the Biped name; not read back.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Was { get; set; }
    }

    public sealed class ChainEntry
    {
        public string Mff { get; set; } = "";
        public string? Mho { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Fit { get; set; }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static BoneMapFile Load(string path) =>
        JsonSerializer.Deserialize<BoneMapFile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path} is empty.");

    public void Save(string path)
    {
        Protected.CheckWrite(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>The primary (non-chain) pairs this file sets.</summary>
    public IEnumerable<(string Mff, string Mho)> Primary() =>
        Bones.Where(b => b.Mho != null && !b.How.Equals("chain", StringComparison.OrdinalIgnoreCase)).Select(b => (b.Mff, b.Mho!));

    /// <summary>What a retarget chose, as a file: every MFF bone that is mapped, in a chain or carries weights.</summary>
    public static BoneMapFile From(Retargeted r, string mhoLabel)
    {
        var m = r.Source ?? throw new InvalidOperationException("a bone map file needs an MFF source");
        var f = new BoneMapFile { Mff = m.Folder, Mho = mhoLabel };
        var chainBones = new HashSet<string>(r.ChainPairs.SelectMany(c => c.Bones), StringComparer.OrdinalIgnoreCase);
        foreach (var b in m.Bones)
        {
            bool mapped = r.Map.TryGetValue(b.Name, out var mho);
            if (!mapped && !b.Deforms) continue;
            string how;
            if (mapped) how = chainBones.Contains(b.Name) ? "chain" : r.FromFile ? "map file"
                : b.Original != null ? (m.Profile == "Guessed" ? "guessed from the shape: check" : $"{m.Profile} name") : "name";
            else
            {
                string? parent = null;
                for (int j = b.Parent; j >= 0 && parent == null; j = m.Bones[j].Parent) r.Map.TryGetValue(m.Bones[j].Name, out parent);
                how = "parent → " + (parent ?? "g_pelvis");
            }
            f.Bones.Add(new BoneEntry { Mff = b.Name, Mho = mapped ? mho : null, How = how, Was = b.Original });
        }
        foreach (var c in r.ChainPairs) f.Chains.Add(new ChainEntry { Mff = c.MffRoot, Mho = c.MhoRoot, Fit = $"off by {c.Fit:0.0} units" });
        foreach (var root in r.UnpairedChains) f.Chains.Add(new ChainEntry { Mff = root, Mho = null, Fit = r.FromFile ? "not paired (map file)" : "no MHO chain near enough" });
        return f;
    }
}
