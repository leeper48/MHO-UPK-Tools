using System.Numerics;
using System.Text.RegularExpressions;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using MhoExtendedModManager;

namespace MhoMffImporter;

/// <summary>
/// A cape or long hair borrowed from another hero for a base hero without one (2026-10-02, preview prototype; Kurt: matched
/// only, the donors named generically, and long hair as well as capes). The donor's bones are grafted on
/// (MhoSkeleton.WithRig) so the MFF model's cape strips or hair strands pair with them, and every animation gets their
/// motion as it loads, matched from the donor's own hand-animated tracks (CapeMatch: the donor frame whose body moves most
/// like it, smoothed). Donors: MHO heroes whose cape / hair is keyframed in nearly every animation (--cape-probe), one rig
/// design each (capes: g_cape1 + two strips; hair: g_hair1 + strands, front strands). Hair is built (0.15.0: the bones in
/// the mesh, the motion in copies of the hero's animation sets, HairAnims); capes are preview only so far.
/// </summary>
sealed class BorrowedRig
{
    public enum Kind { Cape, Hair }

    public required Kind Part;
    public required string Label;           // "Cape 2"
    public required CapeMatch Library;
    public required Dictionary<string, Matrix4x4> Corrections;
    public int Paired;                       // MFF chains that ride it (set after the retarget)
    /// <summary>How much the borrowed swing is exaggerated (Mega Hair 2.5) and smoothed (frames each side; 4: the matched
    /// motion changed 1.35° per frame against the donor's own 0.73°, a jitter from jumping between library frames).</summary>
    public float Amplify = 1;
    public int Smooth = 4;
    /// <summary>Keeps the borrowed bones out of the body (measured on the model after the retarget; MFF_NOCOLLIDE=1: off).</summary>
    public BodyCollide? Collide;
    public int Pushed;

    /// <summary>The body capsules and spreads, measured on the retargeted model (call after the retarget).</summary>
    public void MeasureBody(IReadOnlyList<MeshBone> bones, Retargeted r)
    {
        // hair only: a cape hangs against the back, and a robe or collar on the spine bones makes the torso capsule as wide as
        // the cape's own place (MFF Doctor Strange on Daredevil: 9.4 % of the cape inside the body matched, 16.4 % pushed)
        if (Environment.GetEnvironmentVariable("MFF_NOCOLLIDE") == "1" || (Part == Kind.Cape && Environment.GetEnvironmentVariable("MFF_CAPECOLLIDE") != "1")) return;
        var names = bones.Select(b => b.Name).Where(n => Bones.IsMatch(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Collide = BodyCollide.Measure(bones, r.Sections.SelectMany(s => s.Pos).ToArray(), r.Sections.SelectMany(s => s.Weights).ToArray(), names);
    }

    static readonly Regex CapeBones = new("cape", RegexOptions.IgnoreCase);
    static readonly Regex HairBones = new("hair|bangs|ponytail|braid", RegexOptions.IgnoreCase);
    public static Regex Pattern(Kind k) => k == Kind.Cape ? CapeBones : HairBones;
    /// <summary>Mega Hair's bones: back hair only (g_hair1, g_l / r / c_hair1-3), no front strands or bangs (Kurt: a mane is
    /// back hair; the front strands hang by the face).</summary>
    static readonly Regex BackHair = new(@"^g_([lrc]_)?hair\d+$", RegexOptions.IgnoreCase);
    /// <summary>This rig's bones (the grafted ones match it).</summary>
    public Regex Bones = HairBones;

    /// <summary>The donors, by number (Cape 1 = Thor, Cape 2 = Doctor Strange, Cape 3 = Vision; Hair 1 = Black Widow, Hair 2 =
    /// Psylocke, Hair 3 = Angela: the most keyframed hair of a common rig, short to chest-long).</summary>
    public static readonly string[] CapeDonors = ["UC__MarvelPlayer_Thor_SF.upk", "UC__MarvelPlayer_DoctorStrange_SF.upk", "UC__MarvelPlayer_Vision_SF.upk"];
    public static readonly string[] HairDonors = ["UC__MarvelPlayer_BlackWidow_SF.upk", "UC__MarvelPlayer_Psylocke_SF.upk", "UC__MarvelPlayer_Angela_SF.upk"];
    /// <summary>Mega Hair (Hair ▾'s 4th; Kurt, for Scream / Medusa-like manes): Hair 3's strands stretched to the model's own
    /// hair length, so a waist-long mane has strands reaching it (its lone hair bones then find one nearby).</summary>
    public const int MegaHair = 4;
    public const int MegaSmooth = 8;
    public static string Title(Kind part, int number) => part == Kind.Hair && number == MegaHair ? "Mega Hair" : $"{part} {number}";

    /// <summary>How far an MFF model's hair hangs below its head joint, as a share of its height (its hair bones: named like
    /// hair, or any non-Biped bone under the head that carries skin); 0 without hair bones.</summary>
    public static float HairDrop(MffModel m)
    {
        int head = m.Bones.FindIndex(b => b.Name.Equals("Bip001 Head", StringComparison.OrdinalIgnoreCase));
        if (head < 0 || m.SourceHeight <= 0) return 0;
        bool Under(int i) { for (int j = m.Bones[i].Parent; j >= 0; j = m.Bones[j].Parent) if (j == head) return true; return false; }
        var hair = Enumerable.Range(0, m.Bones.Count).Where(i => Under(i) && !m.Bones[i].Name.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase)
            && (HairBones.IsMatch(m.Bones[i].Name) || m.Bones[i].Deforms)).ToList();
        if (hair.Count == 0) return 0;
        float low = hair.Min(i => m.Bones[i].Position.Z);
        return Math.Max(0, (m.Bones[head].Position.Z - low) / (m.SourceHeight * MffModel.UnitScale));
    }

    static readonly Dictionary<string, CapeMatch> libraries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The borrowed tracks put into <paramref name="a"/> (the skeleton <paramref name="bones"/> holds the grafted bones).</summary>
    public BoneAnimation Apply(BoneAnimation a, IReadOnlyList<MeshBone> bones)
    {
        var matched = Library.Apply(a, bones, ModAnimations.TranslationBones(a), Smooth, null, null, Corrections, Amplify);
        if (Part == Kind.Hair) matched = Lag(matched, bones);
        if (Collide == null) return matched;
        var kept = Collide.Apply(matched, bones, out int pushed);
        Pushed = pushed;
        return kept;
    }

    /// <summary>
    /// Inertia for the hair roots (0.15.2, Kurt: Mega Hair bounced side to side in movement_run 30-60). The matched tracks
    /// are local to the head, so the mane pivoted rigidly with every sway of the hero's head: Carnage's run turns his head
    /// ±30° across every 3 frames, and the hair root followed exactly (the trace: -50° → +15° → -49° …), whatever the
    /// matching did. Real long hair lags: each root's direction in the character's space is averaged over ±LagFrames
    /// (MFF_HAIR_LAG; 0 = off) and turned back into a rotation under the head, at most LagMax degrees (MFF_HAIR_LAG_MAX) from
    /// the matched one so it can't swing into the face. The strands below keep their own matched turns.
    /// </summary>
    BoneAnimation Lag(BoneAnimation m, IReadOnlyList<MeshBone> bones)
    {
        int win = int.TryParse(Environment.GetEnvironmentVariable("MFF_HAIR_LAG"), out int lw) ? lw : LagFrames;
        float maxDeg = float.TryParse(Environment.GetEnvironmentVariable("MFF_HAIR_LAG_MAX"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lm) ? lm : LagMax;
        if (win <= 0) return m;
        var roots = Enumerable.Range(0, bones.Count).Where(i => Bones.IsMatch(bones[i].Name) && m.Tracks.ContainsKey(bones[i].Name)
            && !(bones[i].ParentIndex >= 0 && bones[i].ParentIndex != i && Bones.IsMatch(bones[bones[i].ParentIndex].Name))).ToList();
        if (roots.Count == 0) return m;
        var (frames, _) = MeshAnimator.Span(m);
        int n = Math.Max(1, (int)MathF.Ceiling(frames));
        var poser = new MeshAnimator(bones, [], [], []);
        static Quaternion Q(Matrix4x4 x) { x.Translation = Vector3.Zero; return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(x)); }
        var g = roots.ToDictionary(r => r, _ => new Quaternion[n + 1]);
        var gp = roots.ToDictionary(r => r, _ => new Quaternion[n + 1]);
        for (int f = 0; f <= n; f++)
        {
            poser.Pose(m, Math.Min(f, frames));
            foreach (int r in roots) { g[r][f] = Q(poser.BoneMatrix(r)); gp[r][f] = Q(poser.BoneMatrix(bones[r].ParentIndex)); }
        }
        var tracks = new Dictionary<string, BoneTrack>(m.Tracks, StringComparer.OrdinalIgnoreCase);
        foreach (int r in roots)
        {
            var old = m.Tracks[bones[r].Name].RotationKeys;
            var keys = new List<BoneRotationKey>();
            for (int f = 0; f <= n; f++)
            {
                var sum = Vector4.Zero; var c = g[r][f];
                for (int w = -win; w <= win; w++)
                {
                    var q = g[r][Math.Clamp(f + w, 0, n)]; if (Quaternion.Dot(q, c) < 0) q = -q;
                    sum += new Vector4(q.X, q.Y, q.Z, q.W) * (win + 1 - Math.Abs(w));
                }
                var gs = Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
                // local = global × parent⁻¹ (row vectors, as the poser's matrices)
                Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(gp[r][f]), out var ip);
                var local = Q(Matrix4x4.CreateFromQuaternion(gs) * ip);
                // capped: at most maxDeg from the matched local turn
                var matchedLocal = Quaternion.Normalize(old[Math.Min(f, old.Count - 1)].Rotation);
                if (Quaternion.Dot(local, matchedLocal) < 0) local = -local;
                float ang = 2 * MathF.Acos(Math.Clamp(Quaternion.Dot(local, matchedLocal), -1, 1)) * 180 / MathF.PI;
                if (ang > maxDeg) local = Quaternion.Slerp(matchedLocal, local, maxDeg / ang);
                if (keys.Count > 0 && Quaternion.Dot(keys[^1].Rotation, local) < 0) local = -local;
                keys.Add(new BoneRotationKey(Math.Min(f, frames), local));
            }
            tracks[bones[r].Name] = new BoneTrack { RotationKeys = keys, PositionKeys = m.Tracks[bones[r].Name].PositionKeys };
        }
        return new BoneAnimation { Name = m.Name, DurationSeconds = m.DurationSeconds, Tracks = tracks };
    }

    public const int LagFrames = 6;
    public const float LagMax = 40;

    /// <summary>The skeleton with donor <paramref name="number"/>'s cape or hair grafted on, and what to put into its animations;
    /// the skeleton unchanged (and null) when the base hero has its own or the donor can't be used: <paramref name="note"/> says.</summary>
    public static (MhoSkeleton Skeleton, BorrowedRig? Rig) Graft(MhoSkeleton sk, Kind part, int number, out string note, float modelHairDrop = 0)
    {
        var donors = part == Kind.Cape ? CapeDonors : HairDonors;
        string label = Title(part, number);
        bool mega = part == Kind.Hair && number == MegaHair;
        if (mega) number = 3;
        var rigPattern = mega ? BackHair : Pattern(part);
        if (number < 1 || number > donors.Length) { note = $"no {label}"; return (sk, null); }
        if (sk.Bones.Any(b => Pattern(part).IsMatch(b.Name))) { note = $"{sk.Name} has {part.ToString().ToLowerInvariant()} bones of its own: {label} isn't used"; return (sk, null); }
        string pkg = BasePackage.Resolve(donors[number - 1], true);
        var donor = MhoSkeleton.Load(pkg, null);
        // Mega Hair: the strands stretched so they reach as far below the head (as a share of the height) as the model's hair
        float stretch = 1;
        if (mega && modelHairDrop > 0)
        {
            int dh = donor.Find("g_head");
            var dRig = Enumerable.Range(0, donor.Bones.Count).Where(i => rigPattern.IsMatch(donor.Bones[i].Name)).ToList();
            if (dh >= 0 && dRig.Count > 0 && donor.Height > 0)
            {
                float donorDrop = (donor.Pos(dh).Z - dRig.Min(i => donor.Pos(i).Z)) / donor.Height;
                if (donorDrop > 0.005f) stretch = Math.Clamp(modelHairDrop / donorDrop, 1, 8);
            }
        }
        // a hero with its own cape / hair bones keeps them (and their hand-animated motion); nothing is added
        if (sk.Bones.Any(b => rigPattern.IsMatch(b.Name))) { note = $"{label}: not added, {sk.Name} has its own {(part == Kind.Cape ? "cape" : "hair")} bones (kept, with their own motion)"; return (sk, null); }
        var grafted = sk.WithRig(donor, rigPattern, out var corrections, stretch);
        if (grafted == null) { note = $"{label} couldn't be added to {sk.Name}"; return (sk, null); }
        CapeMatch lib;
        lock (libraries)
            if (!libraries.TryGetValue(pkg + "|" + part + (mega ? "|back" : ""), out lib!))
            {
                string cooked = Settings.Current.CookedFolder ?? Settings.Current.StockFolder ?? Path.GetDirectoryName(pkg)!;
                var anims = ModAnimations.For(new MeshRef(Path.GetFileName(pkg), pkg, donor.Name, 0), donor.Bones, [], cooked).Select(ModAnimations.Load).OfType<BoneAnimation>();
                var rigBones = Enumerable.Range(0, donor.Bones.Count).Where(i => rigPattern.IsMatch(donor.Bones[i].Name));
                libraries[pkg + "|" + part + (mega ? "|back" : "")] = lib = CapeMatch.Build(donor.Bones, rigBones, anims);
            }
        int added = grafted.Bones.Count - sk.Bones.Count;
        note = $"{label}: {added} bones added{(stretch > 1.01f ? $", strands stretched ×{stretch:0.0} to the model's hair" : "")}, motion matched from {lib.Frames} frames";
        // Mega Hair is heavier: Angela's run swings her (short) hair root ±30° every 3 frames, and stretched ×5 that flicked a
        // waist-long mane side to side (Kurt, 0.15.2, movement_run 30-60). A wider smoothing window takes out that fast swing
        // and keeps the slow sway (MFF_MEGA_SMOOTH, frames each side).
        int smooth = mega ? (int.TryParse(Environment.GetEnvironmentVariable("MFF_MEGA_SMOOTH"), out int ms) ? ms : MegaSmooth) : 4;
        return (grafted, new BorrowedRig { Part = part, Label = label, Library = lib, Corrections = corrections, Bones = rigPattern, Smooth = smooth });
    }
}
