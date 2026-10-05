using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>The switches of a retarget (each was an A/B test; the defaults are what Kurt checked in game).</summary>
sealed record RetargetOptions
{
    /// <summary>MFF_HELPER: how helper bones' weights are given out: "limb" (all to the limb joint they sit on, default since
    /// 0.4.2), "split" (half to the parent, 0.4.1), "parent" (all to the parent, before 0.4.1).</summary>
    public string Helper { get; init; } = "limb";
    /// <summary>MFF_HANDAIM=1: aim the hand at the middle finger again (off since 0.6.2: it kinked the wrist).</summary>
    public bool HandAim { get; init; }
    /// <summary>MFF_HAIRFALL=0 turns the long-hair rules off (0.6.11).</summary>
    public bool HairFall { get; init; } = true;
    /// <summary>MFF_HAIRCONTACT=0: hair rides on the body by height only, not by how close it rests on it (0.6.15).</summary>
    public bool HairContact { get; init; } = true;
    /// <summary>Hanging hair rides only on torso skin; arm / leg skin takes it only by contact (0.11.7, Scream; MFF_HAIRLIMBS=1 = old).</summary>
    public bool HairOffLimbs { get; init; } = true;
    /// <summary>Hair moves onto the body only near body skin (0.11.7, Scream's mane; MFF_HAIRNEAR=0 = old).</summary>
    public bool HairNearBody { get; init; } = true;
    /// <summary>Leg skin never carries hair (0.11.8, Scream's knees on Medusa; MFF_HAIRLEGS=1 = old).</summary>
    public bool HairOffLegs { get; init; } = true;
    /// <summary>MFF_HANDFRAME=1: the hand takes MHO's bind-hand frame (0.10.1; it turned the hand bone but not what the hand
    /// looked like: off since 0.10.2).</summary>
    public bool HandFrame { get; init; }
    /// <summary>MFF_FOREARMROLL=0: no turn of the forearm about its axis (before 0.10.2).</summary>
    public bool ForearmRoll { get; init; } = true;
    /// <summary>MFF_TWISTLIMB=0: biped twist bones turn with their parent, as before 0.10.3.</summary>
    public bool TwistFollowsLimb { get; init; } = true;
    /// <summary>MFF_FOOTLEVEL=0: the foot is aimed at MHO's ball (0.6.1-0.10.0) instead of keeping the MFF foot's tilt (0.10.1).</summary>
    public bool FootLevel { get; init; } = true;
    /// <summary>MFF_HIPBLEND=0: pelvis weight below the hip joint stays on the pelvis (before 0.10.4).</summary>
    public bool HipBlend { get; init; } = true;
    /// <summary>MFF_SPINESPLIT=0: a two-spine rig's Spine / Spine1 weight on g_spine01 / g_spine02 only (before 0.10.11).</summary>
    public bool SpineSplit { get; init; } = true;
    /// <summary>MFF_TRANSBIND=0: bones the animations place keep MFF's positions in the bind pose (before 0.10.13).</summary>
    public bool TranslationBind { get; init; } = true;
    /// <summary>MFF_NECKAIM=1: the neck is aimed at MHO's neck → head line (before 0.10.14).</summary>
    public bool NeckAim { get; init; }
    /// <summary>MFF_HELPERSKIN=0: a helper bone's weight goes to the joint it sits on, not the limb its skin lies on (before 0.10.20).</summary>
    public bool HelperBySkin { get; init; } = true;
    /// <summary>MFF_HAIRBYBONE=1: skin on hair bones counts as hanging hair however close to the neck (0.10.22 test: a crease
    /// across America's hair, so off).</summary>
    public bool HairByBone { get; init; }
    /// <summary>MFF_ROOTCHAINS=0: chains hanging from the scene root go to the pelvis (before 0.10.6).</summary>
    public bool RootChainsOnTorso { get; init; } = true;
    /// <summary>MFF_CAPESHAPE=1: capes take the MHO cape chain's bind shape (0.10.7 test; little visible change on
    /// Arachknight / Moon Knight, so off).</summary>
    public bool CapeShape { get; init; }
    /// <summary>MFF_CAPETRANSFER=1: weight transfer from the MHO cape (0.10.8 test: the cape bunched up on Arachknight /
    /// Moon Knight, by nearest point and by place on the cape alike, so off).</summary>
    public bool CapeTransfer { get; init; }
    /// <summary>MFF_CAPEFIT=1 (with the transfer): the cape takes the MHO cape's bind shape (0.10.8 test).</summary>
    public bool CapeFit { get; init; }

    public static RetargetOptions FromEnvironment() => new()
    {
        Helper = Environment.GetEnvironmentVariable("MFF_HELPER") is { Length: > 0 } h ? h : "limb",
        HandAim = Environment.GetEnvironmentVariable("MFF_HANDAIM") == "1",
        HairFall = Environment.GetEnvironmentVariable("MFF_HAIRFALL") != "0",
        HairContact = Environment.GetEnvironmentVariable("MFF_HAIRCONTACT") != "0",
        HairOffLimbs = Environment.GetEnvironmentVariable("MFF_HAIRLIMBS") != "1",
        HairNearBody = Environment.GetEnvironmentVariable("MFF_HAIRNEAR") != "0",
        HairOffLegs = Environment.GetEnvironmentVariable("MFF_HAIRLEGS") != "1",
        HandFrame = Environment.GetEnvironmentVariable("MFF_HANDFRAME") == "1",
        ForearmRoll = Environment.GetEnvironmentVariable("MFF_FOREARMROLL") != "0",
        TwistFollowsLimb = Environment.GetEnvironmentVariable("MFF_TWISTLIMB") != "0",
        FootLevel = Environment.GetEnvironmentVariable("MFF_FOOTLEVEL") != "0",
        HipBlend = Environment.GetEnvironmentVariable("MFF_HIPBLEND") != "0",
        SpineSplit = Environment.GetEnvironmentVariable("MFF_SPINESPLIT") != "0",
        TranslationBind = Environment.GetEnvironmentVariable("MFF_TRANSBIND") != "0",
        NeckAim = Environment.GetEnvironmentVariable("MFF_NECKAIM") == "1",
        HelperBySkin = Environment.GetEnvironmentVariable("MFF_HELPERSKIN") != "0",
        HairByBone = Environment.GetEnvironmentVariable("MFF_HAIRBYBONE") == "1",
        RootChainsOnTorso = Environment.GetEnvironmentVariable("MFF_ROOTCHAINS") != "0",
        CapeShape = Environment.GetEnvironmentVariable("MFF_CAPESHAPE") == "1",
        CapeTransfer = Environment.GetEnvironmentVariable("MFF_CAPETRANSFER") == "1",
        CapeFit = Environment.GetEnvironmentVariable("MFF_CAPEFIT") == "1",
    };
}

static partial class Retarget
{
    /// <summary>One retarget, step by step; the steps share the state below.</summary>
    sealed partial class Job
    {
        readonly MffModel m;
        readonly IEnumerable<Part> parts;
        readonly MhoSkeleton sk;
        readonly BoneMapFile? file;
        readonly RetargetOptions opt;
        readonly Retargeted r;
        readonly int nb;

        List<(string Mff, string Mho)>? forcedChains;
        /// <summary>Chains the map file was edited to leave unpaired ("(not paired)" picked in the Bone Map): no borrowed
        /// strand takes them either (0.14.8).</summary>
        HashSet<string>? keptUnpaired;
        /// <summary>MFF Foot bone → ball point (MFF frame), and MFF Foot / Toe0 → side data (step 0).</summary>
        readonly Dictionary<int, Vector3> ballPoint = new();
        readonly Dictionary<int, (int Foot, int Toe, int Ankle, int Ball, float Ratio)> footSide = new();
        /// <summary>Each MFF bone's swing and swung position (step 1).</summary>
        Quaternion[] delta = [];
        Vector3[] newPos = [];
        /// <summary>The Biped-mapped bones (before chains), and MHO bone → position from an MFF chain (step 1b).</summary>
        HashSet<string> primary = null!;
        readonly Dictionary<int, Vector3> chainPos = new();
        readonly List<ChainMatch> matches = new();
        static readonly System.Text.RegularExpressions.Regex LimbJoint = new(@"^g_[lr]_(shoulder|elbow|wrist|hip|knee|ankle)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        /// <summary>How far each MHO bone was moved onto the animations' offsets (TranslationBind), MHO space; the mesh follows.</summary>
        Vector3[] jointShift = [];
        /// <summary>The MHO bone each MFF bone's weights go to, and helper bones' second MHO bone (step 2).</summary>
        int[] target = [];
        readonly Dictionary<int, int> helperTo = new();
        /// <summary>MHO bone → the MFF bone placing it, and the new skeleton's positions (step 3).</summary>
        Dictionary<string, int> mffOf = null!;
        Vector3?[] pos = [];
        /// <summary>Forearm twist bones (step 4).</summary>
        readonly Dictionary<int, (int Elbow, int Wrist, int MElbow, int MTwist, int MWrist, float F)> twist = new();

        public Job(MffModel m, IEnumerable<Part> parts, MhoSkeleton sk, BoneMapFile? file, RetargetOptions opt)
        {
            this.m = m; this.parts = parts; this.sk = sk; this.file = file; this.opt = opt;
            r = new Retargeted { Source = m, Target = sk, FromFile = file != null };
            nb = m.Bones.Count;
        }

        int Mff(string n) => m.Bones.FindIndex(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        Vector3 ToMho(Vector3 p) => Mirror(p) * r.Scale;

        public Retargeted Run()
        {
            MapBones();
            if (!r.Map.ContainsKey("Bip001 Pelvis")) throw new InvalidDataException("the MFF model has no Bip001 Pelvis (not a Biped rig): Phase 2 handles Biped rigs only.");
            // Scale: MFF body height (cm) onto the MHO mesh's height.
            float mffHeight = m.SourceHeight * MffModel.UnitScale;
            r.Scale = sk.Height > 0 && mffHeight > 0 ? sk.Height / mffHeight : 0.5f;
            Feet();
            Swing();
            Chains();
            CapeShape();
            Targets();
            Helpers();
            Skeleton();
            TwistTable();
            var hair = new Hair(this);
            Mesh(hair);
            CapeTransfer();
            hair.RideOnBody();
            FinalNotes();
            return r;
        }

        // --- the bone map ----------------------------------------------------------------------------------------------------
        void MapBones()
        {
            if (file == null)
            {
                foreach (var (a, b) in DefaultMap())
                    if (Mff(a) >= 0 && sk.Find(b) >= 0) r.Map[m.Bones[Mff(a)].Name] = sk.Bones[sk.Find(b)].Name;
                MapDuplicateHeads();
                MapJaw();
            }
            else
            {
                // The file decides: its primary pairs, its chain pairs, nothing guessed. Every name is checked.
                var errors = new List<string>();
                foreach (var (a, b) in file.Primary())
                {
                    if (Mff(a) < 0) errors.Add($"bones: no MFF bone named '{a}'");
                    else if (sk.Find(b) < 0 && (BorrowedRig.Pattern(BorrowedRig.Kind.Cape).IsMatch(b) || BorrowedRig.Pattern(BorrowedRig.Kind.Hair).IsMatch(b))) { }   // borrowed, off now
                    else if (sk.Find(b) < 0) errors.Add($"bones: '{a}' → no MHO bone named '{b}' in {sk.Name}");
                    else r.Map[m.Bones[Mff(a)].Name] = sk.Bones[sk.Find(b)].Name;
                }
                forcedChains = new();
                keptUnpaired = new(StringComparer.OrdinalIgnoreCase);
                foreach (var c in file.Chains)
                {
                    if (Mff(c.Mff) < 0) { errors.Add($"chains: no MFF bone named '{c.Mff}'"); continue; }
                    if (c.Mho == null) { if (c.Fit == "edited") keptUnpaired.Add(m.Bones[Mff(c.Mff)].Name); continue; }
                    // a borrowed cape / hair bone the file names while that option is off now: that chain is just left unpaired
                    if (sk.Find(c.Mho) < 0 && (BorrowedRig.Pattern(BorrowedRig.Kind.Cape).IsMatch(c.Mho) || BorrowedRig.Pattern(BorrowedRig.Kind.Hair).IsMatch(c.Mho))) continue;
                    if (sk.Find(c.Mho) < 0) { errors.Add($"chains: '{c.Mff}' → no MHO bone named '{c.Mho}' in {sk.Name}"); continue; }
                    if (r.Map.ContainsKey(m.Bones[Mff(c.Mff)].Name)) { errors.Add($"chains: '{c.Mff}' is also mapped in bones (a chain starts at an unmapped bone)"); continue; }
                    forcedChains.Add((m.Bones[Mff(c.Mff)].Name, sk.Bones[sk.Find(c.Mho)].Name));
                }
                if (errors.Count > 0) throw new InvalidDataException("the bone map file has problems:\n  " + string.Join("\n  ", errors));
            }
        }

        /// <summary>
        /// Duplicate head rigs (0.14.2; Captain Marvel S03: Bip001 Neck001 / Head001 and Neck002 / Head002 beside the real ones,
        /// carrying swappable hair parts): a "Bip001 Head&lt;n&gt;" / "Bip001 Neck&lt;n&gt;" on the real bone's joint (within 2 cm;
        /// the height) drives the same MHO bone. Unmapped, their skin fell to the nearest mapped parent, the upper spine, so
        /// that hair followed the chest, and its chains never counted as head chains (no hair pairing).
        /// </summary>
        void MapDuplicateHeads()
        {
            foreach (var real in new[] { "Bip001 Head", "Bip001 Neck" })
            {
                int ri = Mff(real);
                if (ri < 0 || !r.Map.TryGetValue(real, out var mho)) continue;
                var dup = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(real) + @"\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                for (int i = 0; i < m.Bones.Count; i++)
                    if (dup.IsMatch(m.Bones[i].Name) && !r.Map.ContainsKey(m.Bones[i].Name) && (m.Bones[i].Position - m.Bones[ri].Position).Length() < 2f)
                        r.Map[m.Bones[i].Name] = mho;
            }
        }

        /// <summary>Jaw (Kurt, 0.6.9: Gamora S02's jaw was misshapen): MFF's mouth bone has no fixed name (Gamora: "BoneM", under
        /// the head, driving the teeth and lower face); left to chain matching it paired with g_hair1 and the lower face swung
        /// with the hair. The head's own non-Biped child that drives the teeth most (else one named like a jaw / mouth) goes to
        /// g_jaw when the MHO mesh has one.</summary>
        void MapJaw()
        {
            int head = Mff("Bip001 Head"), jaw = sk.Find("g_jaw");
            if (head < 0 || jaw < 0 || r.Map.ContainsValue(sk.Bones[jaw].Name)) return;
            // the head's own extra children; the Biped's mouth bone "Bip001 M" counts too (0.14.6, Kurt: Scream's jaw distorted
            // with Mega Hair: Bip001 M carries her mouth, was left unmapped, and paired with the borrowed hair root)
            var kids = Enumerable.Range(0, m.Bones.Count).Where(i => m.Bones[i].Parent == head
                && (!m.Bones[i].Name.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase) || BipedMouth.IsMatch(m.Bones[i].Name))).ToList();
            var teethW = new Dictionary<int, float>();
            foreach (var p in parts.Where(p => JawPart.IsMatch(p.Name)))
                foreach (var sec in p.Sections)
                    foreach (var vw in sec.Weights)
                        foreach (var w in vw)
                            if (kids.Contains(w.Bone)) teethW[w.Bone] = teethW.GetValueOrDefault(w.Bone) + w.Weight;
            int pick = teethW.Count > 0 ? teethW.MaxBy(x => x.Value).Key : kids.FirstOrDefault(i => JawName.IsMatch(m.Bones[i].Name), -1);
            if (pick >= 0)
            {
                r.Map[m.Bones[pick].Name] = sk.Bones[jaw].Name;
                r.Notes.Add($"jaw: {m.Bones[pick].Name} → {sk.Bones[jaw].Name} ({(teethW.Count > 0 ? "drives the teeth" : "by name")})");
            }
        }

        // --- 0. feet ------------------------------------------------------------------------------------------------------------
        /// <summary>MFF's Toe0 sits at the toe TIP (Punisher: 0.1 cm above the floor at the front), MHO's g_*_ball at the BALL
        /// of the foot. Mapped tip → ball, the boot couldn't bend at the ball (it stayed rigid and dug into the floor when
        /// kneeling: Kurt's "truncated toe") and the ankle→tip line aimed along ankle→ball tilted the shoe up. So the ball
        /// point is put on the MFF foot at the fraction of the foot length MHO's own ball has (measured on the stock mesh:
        /// ankle→ball over ankle→toe tip), the foot is aimed at it, g_*_ball is placed there, and the boot's front vertices
        /// blend onto the ball bone across a band around it (see step 4).</summary>
        void Feet()
        {
            foreach (var sd in new[] { ("L", "l"), ("R", "r") })
            {
                int f = Mff($"Bip001 {sd.Item1} Foot"), t = Mff($"Bip001 {sd.Item1} Toe0");
                int ha = sk.Find($"g_{sd.Item2}_ankle"), hb = sk.Find($"g_{sd.Item2}_ball");
                if (f < 0 || t < 0 || ha < 0 || hb < 0 || !r.Map.ContainsKey(m.Bones[t].Name)) continue;
                float ratio = BallRatio(sk, ha, hb);
                var A = m.Bones[f].Position; var T = m.Bones[t].Position;
                ballPoint[f] = A + ratio * (T - A);
                footSide[f] = footSide[t] = (f, t, ha, hb, ratio);
                r.Notes.Add($"{sd.Item1} foot: ball at {ratio:P0} of ankle → toe tip (from the MHO mesh), toe bone {m.Bones[t].Name} used as the tip");
            }
        }

        // --- 1. swing the MFF skeleton (MFF normalized frame) -------------------------------------------------------------------
        void Swing()
        {
            var aimOf = Aims().Where(x => Mff(x.Bone) >= 0 && Mff(x.Toward) >= 0 && r.Map.ContainsKey(x.Bone) && r.Map.ContainsKey(x.Toward))
                              .ToDictionary(x => Mff(x.Bone), x => Mff(x.Toward));
            delta = new Quaternion[nb]; newPos = new Vector3[nb];
            for (int i = 0; i < nb; i++)   // bones are parent-before-child (built by a tree walk)
            {
                var b = m.Bones[i];
                var qParent = b.Parent >= 0 ? delta[b.Parent] : Quaternion.Identity;
                // Cloth hanging off a swung limb or the spine (an extra bone right under a mapped arm / spine bone) keeps hanging
                // as it did: it moves with its attachment point but doesn't turn with the limb. Turned rigidly, Storm's cape
                // strips (hanging from her forearms beside her legs) swung through her body when the arms were lowered.
                // Under the hands (weapons) and head (hair) the rigid turn is kept.
                if (b.Parent >= 0 && !r.Map.ContainsKey(b.Name) && r.Map.TryGetValue(m.Bones[b.Parent].Name, out var anchor) && !RigidUnder.IsMatch(anchor))
                    qParent = Quaternion.Identity;
                // Biped twist bones hang beside their limb, not under it (ForeTwist under the upper arm, UpArmTwist under the
                // clavicle, ThighTwist under the pelvis): they turn with the limb they lie along (0.10.3; America Chavez's wrist,
                // up to 80 % on ForeTwist, turned with the upper arm while her hand turned with the forearm: a kink at the wrist).
                int from = b.Parent;
                if (opt.TwistFollowsLimb && TwistLimb(i) is int limb && limb < i) { from = limb; qParent = delta[limb]; }
                // A bone hanging from the model root but starting on a mapped joint turns with that joint (0.10.9: Spider-Man's
                // hand bones BoneHL1_01 / BoneHR1_01 sit on his hands; unturned, the hand stayed in MFF's pose: spikes).
                else if (opt.RootChainsOnTorso && RootOnJoint(i) is int jj && jj < i) { from = jj; qParent = delta[jj]; }
                newPos[i] = from >= 0 ? newPos[from] + Vector3.Transform(b.Position - m.Bones[from].Position, qParent) : b.Position;
                var q = qParent;
                // The hand isn't aimed (0.6.2): swung onto MHO's wrist → middle-finger line (MHO's bind hand is bent 10.8°, MFF's
                // 2.4°), the wrist kinked in the rest pose (Kurt saw it in the Mod Manager). It keeps the forearm's turn, as MFF
                // has it.
                bool isHand = b.Name.EndsWith(" Hand", StringComparison.OrdinalIgnoreCase);
                if (isHand && opt.HandFrame && HandFrame(i) is Quaternion hf)
                {
                    // The hand takes MHO's whole hand orientation (0.10.1; America Chavez on Captain Marvel: the palm faced
                    // backward): the swings above set the arm's direction but not its roll, so the hand inherited a twisted
                    // forearm. Direction (wrist → middle knuckle) and roll (index → pinky knuckles) now match MHO's bind hand.
                    q = hf;
                }
                else if (ballPoint.ContainsKey(i) && opt.FootLevel && aimOf.TryGetValue(i, out int fc))
                {
                    // Feet keep the MFF foot's tilt (the sole as modelled) and only turn to face where MHO's foot faces
                    // (0.10.1; America's sneaker, ankle → toe -64.6 deg, aimed at Captain Marvel's heel, ankle → ball -31 deg,
                    // tipped the toes up by about 30 deg). A pure turn about the vertical, not added to the leg's swing.
                    var cur = ballPoint[i] - b.Position;
                    var want = Mirror(sk.Pos(sk.Find(r.Map[m.Bones[fc].Name])) - sk.Pos(sk.Find(r.Map[b.Name])));
                    cur.Z = 0; want.Z = 0;
                    q = cur.LengthSquared() > 1e-8f && want.LengthSquared() > 1e-8f ? Arc(Vector3.Normalize(cur), Vector3.Normalize(want)) : Quaternion.Identity;
                }
                else if (aimOf.TryGetValue(i, out int c) && (opt.HandAim || !isHand)
                         // The neck isn't aimed (0.10.14; Black Cat S01's neck bone points 18 deg back, MHO's 13 deg forward: aimed, her
                         // head bowed 31 deg): head and neck keep their MFF angle to the chest. MFF_NECKAIM=1: aimed (before).
                         && (opt.NeckAim || !b.Name.Equals("Bip001 Neck", StringComparison.OrdinalIgnoreCase)))
                {
                    var aimAt = ballPoint.TryGetValue(i, out var bp) ? bp : m.Bones[c].Position;   // feet: at the ball, not the toe tip
                    var cur = Vector3.Transform(aimAt - b.Position, qParent);
                    var want = Mirror(sk.Pos(sk.Find(r.Map[m.Bones[c].Name])) - sk.Pos(sk.Find(r.Map[b.Name])));   // MHO → MFF frame
                    if (cur.LengthSquared() > 1e-8f && want.LengthSquared() > 1e-8f) q = Arc(Vector3.Normalize(cur), Vector3.Normalize(want)) * qParent;
                    // The forearm turns about its own axis (pronation) so the hand it carries faces like MHO's (0.10.2; America
                    // Chavez on Captain Marvel: the swings set direction but not roll, the hand came out a quarter turn off).
                    // The hand keeps its MFF bend against the forearm, as modelled.
                    if (opt.ForearmRoll && b.Name.EndsWith(" Forearm", StringComparison.OrdinalIgnoreCase) && want.LengthSquared() > 1e-8f && ForearmRoll(i, q, Vector3.Normalize(want)) is Quaternion roll)
                        q = roll * q;
                }
                delta[i] = Quaternion.Normalize(q);
            }
        }

        /// <summary>For a bone whose ancestors are all unmapped (it hangs from the model root): the mapped Biped bone whose joint it
        /// starts on (within 1.5 cm), else null.</summary>
        int? RootOnJoint(int i)
        {
            if (r.Map.ContainsKey(m.Bones[i].Name)) return null;
            for (int j = m.Bones[i].Parent; j >= 0; j = m.Bones[j].Parent) if (r.Map.ContainsKey(m.Bones[j].Name)) return null;
            int best = -1; float bd = 1.5f * 1.5f;
            for (int k = 0; k < nb; k++)
                if (r.Map.ContainsKey(m.Bones[k].Name) && m.Bones[k].Name.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase))
                {
                    float d = (m.Bones[k].Position - m.Bones[i].Position).LengthSquared();
                    if (d < bd) { bd = d; best = k; }
                }
            return best >= 0 ? best : null;
        }

        /// <summary>The limb bone a biped twist bone lies along (ForeTwist → Forearm, UpArmTwist → UpperArm, ThighTwist → Thigh,
        /// CalfTwist → Calf), when that limb isn't already its parent; null otherwise. Only the first bone of a twist chain
        /// (ForeTwist, not ForeTwist1): the rest follow it as children.</summary>
        int? TwistLimb(int i)
        {
            var mt = System.Text.RegularExpressions.Regex.Match(m.Bones[i].Name, @"^Bip001 ?([LR]) ?(ForeTwist|UpArmTwist|ThighTwist|CalfTwist)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!mt.Success) return null;
            string limbName = mt.Groups[2].Value.ToLowerInvariant() switch { "foretwist" => "Forearm", "uparmtwist" => "UpperArm", "thightwist" => "Thigh", _ => "Calf" };
            int limb = Mff($"Bip001 {mt.Groups[1].Value.ToUpperInvariant()} {limbName}");
            return limb >= 0 && limb != m.Bones[i].Parent ? limb : null;
        }

        /// <summary>The turn about the forearm's (new) axis that lines the hand's knuckle line (index → pinky; three-finger rigs:
        /// index → Finger2), carried by forearm swing <paramref name="q"/>, up with MHO's (g_*_index1 → g_*_pinky1 / ring1),
        /// both seen along the forearm. Null when the bones are missing or the lines run along the forearm.</summary>
        Quaternion? ForearmRoll(int forearm, Quaternion q, Vector3 axis)
        {
            string side = m.Bones[forearm].Name.Contains(" L ", StringComparison.Ordinal) ? "L" : "R", ms = side.ToLowerInvariant();
            int idx = Mff($"Bip001 {side} Finger1");
            int last = new[] { 4, 3, 2 }.Select(k => Mff($"Bip001 {side} Finger{k}")).FirstOrDefault(k => k >= 0, -1);
            int hi = sk.Find($"g_{ms}_index1");
            int hl = new[] { "pinky1", "ring1" }.Select(n => sk.Find($"g_{ms}_{n}")).FirstOrDefault(k => k >= 0, -1);
            if (idx < 0 || last < 0 || hi < 0 || hl < 0) return null;
            var a = Vector3.Transform(m.Bones[idx].Position - m.Bones[last].Position, q);
            var t = Mirror(sk.Pos(hi) - sk.Pos(hl));
            a -= axis * Vector3.Dot(a, axis); t -= axis * Vector3.Dot(t, axis);
            if (a.LengthSquared() < 1e-6f || t.LengthSquared() < 1e-6f) return null;
            a = Vector3.Normalize(a); t = Vector3.Normalize(t);
            float angle = MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(a, t)), Vector3.Dot(a, t));
            if (Environment.GetEnvironmentVariable("MFF_DEBUG") == "1") r.Notes.Add($"forearm roll {ms}: {angle * 180 / MathF.PI:0.0} deg");
            return Quaternion.CreateFromAxisAngle(axis, angle);
        }

        /// <summary>The rotation taking the MFF hand's frame (wrist → middle knuckle; index → pinky knuckles) onto MHO's bind
        /// hand's (g_*_wrist → g_*_birdy1; g_*_index1 → g_*_pinky1, else the last finger there is), in the MFF frame; null when
        /// either hand lacks those bones.</summary>
        Quaternion? HandFrame(int hand)
        {
            string side = m.Bones[hand].Name.Contains(" L ", StringComparison.Ordinal) ? "L" : "R", ms = side.ToLowerInvariant();
            int mid = Mff($"Bip001 {side} Finger2"), idx = Mff($"Bip001 {side} Finger1");
            // the outer edge of the hand: pinky, else ring, else (three-finger rigs: America Chavez has Finger0-2) Finger2
            int last = new[] { 4, 3, 2 }.Select(k => Mff($"Bip001 {side} Finger{k}")).FirstOrDefault(k => k >= 0, -1);
            int hw = sk.Find($"g_{ms}_wrist"), hm = sk.Find($"g_{ms}_birdy1"), hi = sk.Find($"g_{ms}_index1");
            int hl = new[] { "pinky1", "ring1" }.Select(n => sk.Find($"g_{ms}_{n}")).FirstOrDefault(k => k >= 0, -1);
            if (mid < 0 || idx < 0 || last < 0 || hw < 0 || hm < 0 || hi < 0 || hl < 0) return null;
            static Matrix4x4? Basis(Vector3 dir, Vector3 across)
            {
                if (dir.LengthSquared() < 1e-8f || across.LengthSquared() < 1e-8f) return null;
                var x = Vector3.Normalize(dir);
                var z = Vector3.Cross(x, across);
                if (z.LengthSquared() < 1e-8f) return null;
                z = Vector3.Normalize(z);
                var y = Vector3.Cross(z, x);
                return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
            }
            var from = Basis(m.Bones[mid].Position - m.Bones[hand].Position, m.Bones[idx].Position - m.Bones[last].Position);
            // MHO → MFF frame: Mirror (Y negated). A mirror flips handedness, so the across vector's cross product would turn the
            // basis inside out; mirroring both vectors and building the basis from them keeps it a rotation in the MFF frame.
            var to = Basis(Mirror(sk.Pos(hm) - sk.Pos(hw)), Mirror(sk.Pos(hi) - sk.Pos(hl)));
            if (from is not { } f || to is not { } t) return null;
            // rows are the basis vectors: R maps from's rows onto to's rows = transpose(from) * to in row-vector terms
            var rot = Matrix4x4.Transpose(f) * t;
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rot));
        }

        // --- 1b. extra chains (capes, hair, tails): MFF chain → the MHO hero's own chain by shape and position --------------------
        void Chains()
        {
            primary = r.Map.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mhoMapped = r.Map.Values.Select(sk.Find).Where(i => i >= 0).ToHashSet();
            foreach (var match in MatchChains(m, newPos, ToMho, sk, mhoMapped, r, forcedChains, keptUnpaired))
            {
                matches.Add(match);
                r.ChainPairs.Add((m.Bones[match.BoneMap[0].Mff].Name, sk.Bones[match.Positions[0].Mho].Name, match.Fit,
                                  match.BoneMap.Select(x => m.Bones[x.Mff].Name).ToList()));
                foreach (var (mffBone, mhoBone) in match.BoneMap) r.Map[m.Bones[mffBone].Name] = sk.Bones[mhoBone].Name;
                // With the weight transfer, MHO cape bones stay where the MHO skeleton has them (as on its armature in Blender).
                if (!(opt.CapeTransfer && IsCape(match)) && !match.Shared)
                    foreach (var (mhoBone, p) in match.Positions) chainPos[mhoBone] = p;
                r.Chains.Add(match.Describe);
            }
        }

        /// <summary>Capes take the MHO cape's bind shape (0.10.7; Kurt: Arachknight's cape on Moon Knight hung straight down
        /// and stayed narrow while Moon Knight's own, flared out behind him in his bind pose, swung wide like wings: the MHO
        /// animation turns the cape bones from MHO's shape). Each MFF strip paired with an MHO chain named *cape* is swung,
        /// segment by segment, to point where the MHO chain points at the same place along it; its lengths and where it hangs
        /// from stay MFF's. The MHO cape bones are then put on the reshaped strip.</summary>
        void CapeShape()
        {
            if (!opt.CapeShape) return;
            foreach (var match in matches)
            {
                var ca = match.BoneMap.Select(x => x.Mff).ToList();
                var cb = match.Positions.Select(x => x.Mho).ToList();
                if (ca.Count < 2 || !cb.Any(h => sk.Bones[h].Name.Contains("cape", StringComparison.OrdinalIgnoreCase))) continue;
                var pb = cb.Select(sk.Pos).ToList();
                var tb = Params(pb);
                var ta = Params(ca.Select(i => ToMho(newPos[i])).ToList());
                var moved = new HashSet<int>();
                for (int k = 0; k < ca.Count; k++)
                {
                    int i = ca[k];
                    var qPrev = k == 0 ? delta[i] : delta[ca[k - 1]];
                    if (k > 0) newPos[i] = newPos[ca[k - 1]] + Vector3.Transform(m.Bones[i].Position - m.Bones[ca[k - 1]].Position, delta[ca[k - 1]]);
                    if (k + 1 < ca.Count)
                    {
                        var cur = Vector3.Transform(m.Bones[ca[k + 1]].Position - m.Bones[i].Position, qPrev);
                        var want = Mirror(At(pb, ta[k + 1]) - At(pb, ta[k]));
                        delta[i] = cur.LengthSquared() > 1e-8f && want.LengthSquared() > 1e-8f ? Quaternion.Normalize(Arc(Vector3.Normalize(cur), Vector3.Normalize(want)) * qPrev) : qPrev;
                    }
                    else delta[i] = qPrev;
                    moved.Add(i);
                }
                // anything hanging under the strip follows it rigidly
                for (int i = 0; i < nb; i++)
                    if (!moved.Contains(i) && m.Bones[i].Parent >= 0 && moved.Contains(m.Bones[i].Parent))
                    {
                        int pa = m.Bones[i].Parent;
                        delta[i] = delta[pa];
                        newPos[i] = newPos[pa] + Vector3.Transform(m.Bones[i].Position - m.Bones[pa].Position, delta[pa]);
                        moved.Add(i);
                    }
                var pts = ca.Select(i => ToMho(newPos[i])).ToList();
                for (int j = 0; j < cb.Count; j++) chainPos[cb[j]] = At(pts, tb[j]);
            }
        }

        /// <summary>An unmapped MFF bone under the head (hair strands, ponytail).</summary>
        bool IsHairBone(int b)
        {
            if (r.Map.ContainsKey(m.Bones[b].Name) && !HairLike.IsMatch(m.Bones[b].Name)) return false;
            for (int k = m.Bones[b].Parent; k >= 0; k = m.Bones[k].Parent)
                if (m.Bones[k].Name.Equals("Bip001 Head", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        bool IsCape(ChainMatch match) => match.Positions.Any(x => sk.Bones[x.Mho].Name.Contains("cape", StringComparison.OrdinalIgnoreCase));

        /// <summary>Cape weight transfer (0.10.8; Kurt's Blender method: lay the two models over each other and transfer the
        /// weights). Each MFF cape vertex (weighted to strips paired with MHO cape chains) takes the weights of the nearest stock
        /// MHO vertex that rides the MHO cape bones, blended by how much of it was on the strips. Moon Knight's cape has 7
        /// chains, Arachknight's 4 strips: on the strips alone the cape stayed narrow and the side on the body stayed rigid.</summary>
        void CapeTransfer()
        {
            if (!opt.CapeTransfer || !matches.Any(IsCape)) return;
            var capeMff = matches.Where(IsCape).SelectMany(x => x.BoneMap.Select(b => b.Mff)).ToHashSet();
            var lod = sk.Mesh.HighestDetail;
            if (lod == null) return;
            var sw = MhoAnim.Weights(lod);
            var src = new List<int>();
            for (int v = 0; v < lod.Positions.Count; v++)
                if (sw[v].Where(x => sk.Bones[x.Item1].Name.Contains("cape", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Item2) >= 0.3f) src.Add(v);
            if (src.Count == 0) return;
            // Matched by place on the cape, not raw distance (the MHO cape flares out; nearest points took a hem vertex to
            // the MHO cape's middle and the cape bunched up): height down the cape (0 top, 1 hem) and the angle around the
            // body's vertical axis.
            var axis = sk.Pos(sk.Find("g_pelvis"));
            var dst = new List<(int Sec, int V, float C)>();
            {
                int si = 0;
                foreach (var s0 in parts.SelectMany(p => p.Sections))
                {
                    for (int v = 0; v < s0.Pos.Length; v++)
                    {
                        float c0 = s0.Weights[v].Where(x => capeMff.Contains(x.Bone)).Sum(x => x.Weight), t0 = s0.Weights[v].Sum(x => x.Weight);
                        if (c0 > 0 && t0 > 0) dst.Add((si, v, Math.Clamp(c0 / t0, 0, 1)));
                    }
                    si++;
                }
            }
            if (dst.Count == 0) return;
            (float Lo, float Hi) Span(IEnumerable<float> z) { var l = z.ToList(); return (l.Min(), l.Max()); }
            var (sLo, sHi) = Span(src.Select(k => lod.Positions[k].Z));
            var (dLo, dHi) = Span(dst.Select(d => r.Sections[d.Sec].Pos[d.V].Z));
            (float H, float A) Place(Vector3 q, float lo, float hi) => ((hi - q.Z) / Math.Max(1e-3f, hi - lo), MathF.Atan2(q.Y - axis.Y, q.X - axis.X));
            var srcPlace = src.Select(k => Place(lod.Positions[k], sLo, sHi)).ToArray();
            float Dist((float H, float A) a, (float H, float A) b)
            {
                float da = MathF.Abs(a.A - b.A); if (da > MathF.PI) da = 2 * MathF.PI - da;
                return (a.H - b.H) * (a.H - b.H) + (da / MathF.PI) * (da / MathF.PI);
            }
            int changed = 0;
            foreach (var (sec, v, c) in dst)
            {
                var rs = r.Sections[sec];
                var me = Place(rs.Pos[v], dLo, dHi);
                // the 4 nearest MHO cape points by place, blended by inverse distance (like Blender's interpolated mapping)
                var near = Enumerable.Range(0, src.Count).Select(k => (k, d: Dist(me, srcPlace[k]))).OrderBy(x => x.d).Take(4).ToList();
                var iw = near.Select(x => 1f / (MathF.Sqrt(x.d) + 1e-3f)).ToList(); float iws = iw.Sum();
                var acc = new Dictionary<int, float>();
                foreach (var (b, w) in rs.Weights[v]) acc[b] = acc.GetValueOrDefault(b) + w * (1 - c);
                for (int n = 0; n < near.Count; n++)
                    foreach (var (b, w) in sw[src[near[n].k]]) acc[b] = acc.GetValueOrDefault(b) + w * c * iw[n] / iws;
                // Fit (MFF_CAPEFIT=1): the cape vertex moves onto the MHO cape at that place, so it sits on the MHO cape
                // bones it now follows (the Blender add-on session's hypothesis: cloth far from its bones' bind pivots swings
                // on long arcs and wraps the body).
                if (opt.CapeFit)
                {
                    var at = Vector3.Zero;
                    for (int n = 0; n < near.Count; n++) at += lod.Positions[src[near[n].k]] * (iw[n] / iws);
                    rs.Pos[v] = Vector3.Lerp(rs.Pos[v], at, c);
                }
                rs.Weights[v] = Top4(acc);
                changed++;
            }
            r.Notes.Add($"cape: {changed} vertices take weights from the MHO cape ({src.Count} source vertices)");
        }

        // --- 2. the MHO bone each MFF bone's weights go to (nearest mapped ancestor) -------------------------------------------
        void Targets()
        {
            target = new int[nb];
            for (int i = 0; i < nb; i++)
            {
                int j = i;
                int top = i;
                // top: the chain's first bone (not the model's root node, which sits at the feet)
                while (j >= 0 && !r.Map.ContainsKey(m.Bones[j].Name)) { if (m.Bones[j].Parent >= 0) top = j; j = m.Bones[j].Parent; }
                if (j >= 0) target[i] = sk.Find(r.Map[m.Bones[j].Name]);
                else if (opt.RootChainsOnTorso && OnJoint(m.Bones[top].Position) is int jb) target[i] = jb;
                else if (opt.RootChainsOnTorso && TorsoNear(m.Bones[top].Position) is int tb) target[i] = tb;
                else target[i] = sk.Find("g_pelvis");
            }
        }

        /// <summary>The MHO bone of the mapped torso bone (pelvis, spine, neck, clavicle) nearest to an MFF point: for chains
        /// that hang from the scene root (0.10.6; Arachknight's cape strips start at the shoulders but are children of the model
        /// root, so all their weight went to the pelvis and the cape's top didn't follow the chest).</summary>
        /// <summary>The MHO bone of a mapped MFF bone whose joint a root chain starts on (within 1.5 cm): Spider-Man's web strands
        /// (BoneHL1_01 / BoneHR1_01, children of the model root) start exactly on his hands (0.10.9).</summary>
        int? OnJoint(Vector3 p)
        {
            int best = -1; float bd = 1.5f * 1.5f;
            for (int k = 0; k < nb; k++)
                if (r.Map.ContainsKey(m.Bones[k].Name) && primary.Contains(m.Bones[k].Name))
                {
                    float d = (m.Bones[k].Position - p).LengthSquared();
                    if (d < bd) { bd = d; best = k; }
                }
            return best >= 0 && sk.Find(r.Map[m.Bones[best].Name]) is int h && h >= 0 ? h : null;
        }

        int? TorsoNear(Vector3 p)
        {
            var torso = new System.Text.RegularExpressions.Regex(@"^Bip001 (Pelvis|Spine\d*|Neck|[LR] Clavicle)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int best = -1; float bd = float.MaxValue;
            for (int k = 0; k < nb; k++)
                if (torso.IsMatch(m.Bones[k].Name) && r.Map.ContainsKey(m.Bones[k].Name))
                {
                    float d = (m.Bones[k].Position - p).LengthSquared();
                    if (d < bd) { bd = d; best = k; }
                }
            return best >= 0 && sk.Find(r.Map[m.Bones[best].Name]) is int h && h >= 0 ? h : null;
        }

        /// <summary>Helper bones: an unmapped weighted bone sitting on a mapped bone's joint that isn't its ancestor (Punisher's
        /// Bone007 / Bone010 on the thighs, children of the pelvis; Bone001 / Bone004 on the upper arms, children of the
        /// clavicles). In 3ds Max such helpers turn about halfway between parent and limb; on the parent alone the hip and
        /// shoulder pinched in when the limb lifted (Kurt saw the kneeling thigh collapse, 2026-09-30). Their weight goes by
        /// <see cref="RetargetOptions.Helper"/>.</summary>
        Dictionary<int, Vector3>? skinCentre;
        /// <summary>For a helper sitting on Biped joint <paramref name="k"/>: that joint's Biped parent when the vertices helper
        /// <paramref name="i"/> carries most lie along the parent segment (parent → k: inside it, and much nearer the segment than
        /// the joint), else null.</summary>
        int? SkinSegment(int i, int k)
        {
            if (skinCentre == null)
            {
                var sum = new Dictionary<int, (Vector3 S, int N)>();
                foreach (var sec in parts.SelectMany(p => p.Sections))
                    for (int v = 0; v < sec.Pos.Length; v++)
                        if (sec.Weights[v].Length > 0)
                        {
                            int b = sec.Weights[v].MaxBy(x => x.Weight).Bone;
                            var c = sum.GetValueOrDefault(b); sum[b] = (c.S + sec.Pos[v], c.N + 1);
                        }
                skinCentre = sum.Where(kv => kv.Value.N >= 4).ToDictionary(kv => kv.Key, kv => kv.Value.S / kv.Value.N);
            }
            if (!skinCentre.TryGetValue(i, out var at)) return null;
            int p = m.Bones[k].Parent;
            if (p < 0 || !primary.Contains(m.Bones[p].Name) || !m.Bones[p].Name.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase)) return null;
            var a = m.Bones[p].Position; var ab = m.Bones[k].Position - a;
            if (ab.LengthSquared() < 1e-6f) return null;
            float t = Vector3.Dot(at - a, ab) / ab.LengthSquared();
            float dSeg = (a + Math.Clamp(t, 0, 1) * ab - at).Length(), dJoint = (m.Bones[k].Position - at).Length();
            return t > 0.05f && t < 0.8f && dSeg < 0.5f * dJoint ? p : null;
        }

        void Helpers()
        {
            float near = 0.03f * m.SourceHeight * MffModel.UnitScale;
            bool MffAncestor(int a, int b) { for (int k = m.Bones[b].Parent; k >= 0; k = m.Bones[k].Parent) if (k == a) return true; return false; }
            for (int i = 0; i < nb; i++)
            {
                if (!m.Bones[i].Deforms || r.Map.ContainsKey(m.Bones[i].Name)) continue;
                var at = m.Bones[i].Position;
                int best = -1; float bd = float.MaxValue;
                for (int k = 0; k < nb; k++)
                {
                    if (k == i || !primary.Contains(m.Bones[k].Name) || MffAncestor(k, i)) continue;
                    float d = (m.Bones[k].Position - at).Length();
                    if (d < bd) { bd = d; best = k; }
                }
                // The limb its skin lies on (0.10.20; Spider-Man S11's leg skin hangs on bn_l_calf, which sits on the ankle
                // joint but carries the lower leg: on the ankle the calf turned with the foot and the knee buckled): the
                // joint's parent when the vertices it carries lie along the parent segment.
                if (best >= 0 && bd <= near && opt.HelperBySkin && SkinSegment(i, best) is int seg) best = seg;
                if (best >= 0 && bd <= near && sk.Find(r.Map[m.Bones[best].Name]) != target[i] && opt.Helper != "parent")
                {
                    helperTo[i] = sk.Find(r.Map[m.Bones[best].Name]);
                    r.Notes.Add($"helper bone {m.Bones[i].Name} (on {m.Bones[best].Name}, {bd:0.0} cm): weight {(opt.Helper == "split" ? $"split between {sk.Bones[target[i]].Name} and " : "to ")}{r.Map[m.Bones[best].Name]}");
                }
            }
        }

        // --- 3. new reference skeleton (MHO space) --------------------------------------------------------------------------------
        void Skeleton()
        {
            // MHO bone → the MFF bone placing it (the first mapped, when several share one: ForeTwist, ForeTwist1… → g_l_forarm).
            mffOf = r.Map.Where(kv => primary.Contains(kv.Key)).GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.Select(kv => Mff(kv.Key)).Min(), StringComparer.OrdinalIgnoreCase);
            int n = sk.Bones.Count;
            pos = new Vector3?[n];
            jointShift = new Vector3[n];
            for (int i = 0; i < n; i++)
                if (mffOf.TryGetValue(sk.Bones[i].Name, out int mi)) pos[i] = ToMho(newPos[mi]);
                else if (chainPos.TryGetValue(i, out var cp)) pos[i] = cp;
            foreach (var (f, bpt) in ballPoint)   // g_*_ball on the swung ball point
                pos[footSide[f].Ball] = ToMho(newPos[f] + Vector3.Transform(bpt - m.Bones[f].Position, delta[f]));
            // Unmapped MHO bones: between a mapped parent and one of its mapped children (twists), interpolate along the new
            // segment; else keep the MHO offset from the nearest mapped bone (ancestor or descendant) in the MHO rest pose.
            bool IsAncestor(int a, int b) { for (int k = sk.Bones[b].ParentIndex; k >= 0 && k != b; k = sk.Bones[k].ParentIndex) { if (k == a) return true; if (sk.Bones[k].ParentIndex == k) break; } return false; }
            var mappedIdx = Enumerable.Range(0, n).Where(i => pos[i] != null).ToList();
            for (int i = 0; i < n; i++)
            {
                if (pos[i] != null) continue;
                var p = sk.Pos(i);
                // twist: nearest mapped ancestor A and a mapped bone B below A with p near segment A→B
                int a = sk.Bones[i].ParentIndex;
                while (a >= 0 && pos[a] == null && sk.Bones[a].ParentIndex != a) a = sk.Bones[a].ParentIndex;
                bool placed = false;
                if (a >= 0 && pos[a] != null)
                    foreach (int b in mappedIdx.Where(b => IsAncestor(a, b)))
                    {
                        var seg = sk.Pos(b) - sk.Pos(a); float len2 = seg.LengthSquared();
                        if (len2 < 1e-6f) continue;
                        float t = Vector3.Dot(p - sk.Pos(a), seg) / len2;
                        var off = p - (sk.Pos(a) + t * seg);
                        if (t > 0.02f && t < 0.98f && off.Length() < 0.2f * MathF.Sqrt(len2))
                        {
                            pos[i] = Vector3.Lerp(pos[a]!.Value, pos[b]!.Value, t) + off;
                            placed = true; break;
                        }
                    }
                if (placed) continue;
                int best = mappedIdx.Where(j => IsAncestor(j, i) || IsAncestor(i, j)).DefaultIfEmpty(-1).MinBy(j => j < 0 ? float.MaxValue : (sk.Pos(j) - p).LengthSquared());
                pos[i] = best >= 0 ? pos[best]!.Value + (p - sk.Pos(best)) : p;
            }
            // Bones the animations place (0.10.13; Kurt: Spider-Man's shoulders, upper arms and chest grew in motion, not at rest):
            // MHO's AnimSets are rotation-only except for UseTranslationBoneNames (spine, hips, clavicles, shoulders, elbows,
            // bicep twists, pectorals, scapulae …), whose positions come from the animation, i.e. MHO's own offsets. Bound at
            // MFF's places, those joints jumped to MHO's in every animation and the skin around them stretched. They now get
            // MHO's offset from their parent in the bind pose too (parents first).
            if (opt.TranslationBind)
            {
                var placed = MhoAnim.TranslationBones(sk.Package, sk.Bones.Select(b => b.Name));
                // The upper spine goes where MHO's chain spine03 → clavicle → shoulder ends on MFF's shoulder joints (both sides
                // averaged): from our spine03 at MFF's chest, the animations put the shoulders 5.6 units off on Spider-Man.
                int s3 = sk.Find("g_spine03");
                if (s3 >= 0 && placed.Contains("g_spine03") && placed.Contains("g_l_shoulder") && placed.Contains("g_r_shoulder"))
                {
                    var at = Vector3.Zero; int cnt = 0;
                    foreach (var sd in new[] { "l", "r" })
                        if (sk.Find($"g_{sd}_shoulder") is int sh && sh >= 0 && pos[sh] != null) { at += pos[sh]!.Value - (sk.Pos(sh) - sk.Pos(s3)); cnt++; }
                    if (cnt == 2 && pos[s3] != null)
                    {
                        if (Environment.GetEnvironmentVariable("MFF_DEBUG") == "1") r.Notes.Add($"g_spine03 moved {(at / 2 - pos[s3]!.Value).Length():0.00} units to put MHO's shoulders on MFF's");
                        pos[s3] = at / 2;
                    }
                }
                bool IsUnder(int a, int b) { for (int k = sk.Bones[b].ParentIndex; k >= 0 && k != b; k = sk.Bones[k].ParentIndex) { if (k == a) return true; if (sk.Bones[k].ParentIndex == k) break; } return false; }
                for (int i = 0; i < n; i++)
                {
                    int par = sk.Bones[i].ParentIndex;
                    if (par < 0 || par == i || !placed.Contains(sk.Bones[i].Name)) continue;
                    var off = sk.Pos(i) - sk.Pos(par);
                    var want = pos[par]!.Value + off;
                    var d = want - pos[i]!.Value;
                    if (d.Length() < 1e-4f) continue;
                    if (Environment.GetEnvironmentVariable("MFF_DEBUG") == "1" && d.Length() > 0.3f)
                        r.Notes.Add($"placed by the animation: {sk.Bones[i].Name} {d.Length():0.00} units off (from {sk.Bones[par].Name}: ours {(pos[i]!.Value - pos[par]!.Value).Length():0.00}, MHO {off.Length():0.00})");
                    // an unmapped helper parent (g_spine01_offset, g_l_hip_offset …) moves onto the joint instead: MFF's proportions stay
                    if (sk.Bones[par].Name.EndsWith("_offset", StringComparison.OrdinalIgnoreCase) && !mffOf.ContainsKey(sk.Bones[par].Name))
                    {
                        pos[par] = pos[i]!.Value - off;
                        continue;
                    }
                    // else the joint and everything under it move to MHO's offset. The skin follows (by its weights, in Mesh) only for
                    // the limb joints that set a limb's length (shoulder, elbow, hip, knee …); twist / helper bones (g_biceptwist sits
                    // mid upper arm in MHO, at the joint in MFF) and the clavicle only move their joint: a turn about a bone's own
                    // axis doesn't care where along it the joint sits.
                    bool carry = LimbJoint.IsMatch(sk.Bones[i].Name);
                    if (!carry) { pos[i] = want; continue; }   // the joint alone; its children are placed from it in turn
                    for (int k = 0; k < n; k++)
                        if (k == i || IsUnder(i, k)) { pos[k] = pos[k]!.Value + d; jointShift[k] += d; }
                }
            }
            for (int i = 0; i < n; i++)
            {
                var g = sk.BoneToModel[i];
                g.Translation = pos[i]!.Value;
                r.Bones.Add(new RefBone
                {
                    Name = sk.Bones[i].Name, Parent = sk.Bones[i].ParentIndex == i ? -1 : sk.Bones[i].ParentIndex, Global = g,
                    Mapped = mffOf.ContainsKey(sk.Bones[i].Name) || chainPos.ContainsKey(i),
                    From = mffOf.TryGetValue(sk.Bones[i].Name, out int f) ? m.Bones[f].Name : r.Map.FirstOrDefault(kv => kv.Value.Equals(sk.Bones[i].Name, StringComparison.OrdinalIgnoreCase)).Key,
                });
            }
        }

        /// <summary>Forearm twist bones (see step 4): MFF bones mapped to g_*_forarm, with that side's elbow / wrist and where MHO's
        /// twist bone sits along its own forearm (Punisher: about 60 %).</summary>
        void TwistTable()
        {
            foreach (var sd in new[] { ("L", "l"), ("R", "r") })
            {
                int e = Mff($"Bip001 {sd.Item1} Forearm"), wr = Mff($"Bip001 {sd.Item1} Hand");
                int he = sk.Find($"g_{sd.Item2}_elbow"), ht = sk.Find($"g_{sd.Item2}_forarm"), hw = sk.Find($"g_{sd.Item2}_wrist");
                if (e < 0 || wr < 0 || he < 0 || ht < 0 || hw < 0) continue;
                var seg = sk.Pos(hw) - sk.Pos(he);
                float f = Math.Clamp(Vector3.Dot(sk.Pos(ht) - sk.Pos(he), seg) / Math.Max(1e-6f, seg.LengthSquared()), 0.05f, 0.95f);
                for (int i = 0; i < nb; i++)
                    if (r.Map.TryGetValue(m.Bones[i].Name, out var mho) && mho.Equals(sk.Bones[ht].Name, StringComparison.OrdinalIgnoreCase))
                        twist[i] = (e, wr, he, ht, hw, f);
            }
        }

        // --- 4. mesh: skin to the swung MFF skeleton, then into MHO space with MHO weights ---------------------------------------
        void Mesh(Hair hair)
        {
            foreach (var s in parts.SelectMany(p => p.Sections))
            {
                int nv = s.Pos.Length;
                var pp = new Vector3[nv]; var nn = new Vector3[nv];
                var w = new (int, float)[nv][];
                for (int v = 0; v < nv; v++)
                {
                    var inf = s.Weights[v];
                    if (inf.Length == 0) { pp[v] = ToMho(s.Pos[v]); nn[v] = Mirror(s.Normal[v]); w[v] = [(sk.Find("g_pelvis"), 1f)]; continue; }
                    Vector3 sp = Vector3.Zero, sn = Vector3.Zero; float tot = 0;
                    var acc = new Dictionary<int, float>();
                    foreach (var (bone, weight) in inf)
                    {
                        sp += weight * (newPos[bone] + Vector3.Transform(s.Pos[v] - m.Bones[bone].Position, delta[bone]));
                        sn += weight * Vector3.Transform(s.Normal[v], delta[bone]);
                        tot += weight;
                        WeightTo(acc, bone, weight, s.Pos[v]);
                    }
                    pp[v] = ToMho(sp / tot);
                    nn[v] = sn.LengthSquared() > 0 ? Vector3.Normalize(Mirror(sn)) : Mirror(s.Normal[v]);
                    hair.Fall(acc, pp[v], r.Sections.Count, v, opt.HairByBone && inf.Any(x => x.Weight > 0.2f && IsHairBone(x.Bone)));
                    w[v] = Top4(acc);
                    // joints moved onto the animations' offsets (TranslationBind) carry the skin with them, by its weights
                    foreach (var (jb, jw) in w[v]) pp[v] += jw * jointShift[jb];
                }
                r.Sections.Add(new RefSection { Material = s.Material, Tex = s.Tex, Pos = pp, Normal = nn, Uv = s.Uv, Tris = (int[])s.Tris.Clone(), Weights = w });
            }
        }

        /// <summary>Where one MFF influence's weight goes among MHO bones (<paramref name="p"/>: the vertex, MFF frame).</summary>
        void WeightTo(Dictionary<int, float> acc, int bone, float weight, Vector3 p)
        {
            if (helperTo.TryGetValue(bone, out int second))
            {
                // Helper mode: "limb" (default since 0.4.2, Kurt: skip the helpers) gives it all to the limb it sits on;
                // "split" halves it between parent and limb (0.4.1).
                float toLimb = opt.Helper == "split" ? 0.5f : 1f;
                if (toLimb < 1) acc[target[bone]] = acc.GetValueOrDefault(target[bone]) + weight * (1 - toLimb);
                acc[second] = acc.GetValueOrDefault(second) + weight * toLimb;
            }
            else if (twist.TryGetValue(bone, out var tw))
            {
                // Forearm twist (MFF ForeTwist* → g_*_forarm): by the vertex's place along elbow → wrist, blended elbow → forarm
                // above MHO's twist bone and forarm → wrist below it, as a twist rig spreads the hand's turn. All on g_*_forarm,
                // the cuff lagged behind the glove and the wrist seam opened (Kurt, 0.6.0).
                var E = m.Bones[tw.Elbow].Position; var Wp = m.Bones[tw.Wrist].Position;
                var seg = Wp - E; float len2 = seg.LengthSquared();
                float t = len2 > 0 ? Math.Clamp(Vector3.Dot(p - E, seg) / len2, 0, 1) : tw.F;
                if (t <= tw.F) { float u = tw.F > 0 ? t / tw.F : 1; Add(acc, tw.MElbow, weight * (1 - u)); Add(acc, tw.MTwist, weight * u); }
                else { float u = (t - tw.F) / (1 - tw.F); Add(acc, tw.MTwist, weight * (1 - u)); Add(acc, tw.MWrist, weight * u); }
            }
            else if (opt.SpineSplit && SpineRamp(bone, p) is (int, float)[] ramp)
            {
                // Two-spine MFF rigs (0.10.11-0.10.12; Spider-Man: Spine → g_spine01, Spine1 → g_spine02, g_spine03 no weight):
                // MHO's animations bend all three spine bones (g_spine03 carries the clavicles and neck), and the side creased
                // into a point under a raised arm. Spine and Spine1 weight ramps across g_spine01 → 02 → 03 by height.
                foreach (var (b, f) in ramp) Add(acc, b, weight * f);
            }
            else if (opt.HipBlend && HipShare(bone, p) is (int thighTo, float hs) && hs > 0)
            {
                // Pelvis weight below the hip joint (0.10.4; the shorts' hem, half on the pelvis, poked out of America
                // Chavez's thigh in a wide stance): handed to that side's thigh over the top fifth of the thigh.
                Add(acc, target[bone], weight * (1 - hs)); Add(acc, thighTo, weight * hs);
            }
            else if (footSide.TryGetValue(bone, out var fs))
            {
                // Feet (see step 0): Foot and Toe0 weight goes to ankle / ball by the vertex's place along ankle → toe tip,
                // blended across ±8 % of the foot length around the ball.
                var A = m.Bones[fs.Foot].Position; var T = m.Bones[fs.Toe].Position;
                var seg = T - A; float len2 = seg.LengthSquared();
                float t = len2 > 0 ? Vector3.Dot(p - A, seg) / len2 : 0;
                float u = Math.Clamp((t - (fs.Ratio - 0.08f)) / 0.16f, 0, 1);
                u = u * u * (3 - 2 * u);
                Add(acc, fs.Ankle, weight * (1 - u)); Add(acc, fs.Ball, weight * u);
            }
            else acc[target[bone]] = acc.GetValueOrDefault(target[bone]) + weight;
        }

        /// <summary>For Bip001 Spine / Spine1 weight in a rig without Spine2: the share of each of MHO's g_spine01 / 02 / 03 at
        /// <paramref name="p"/>, linear between their places on the MFF torso (pelvis → neck at MHO's own ratios; below
        /// g_spine01's place all g_spine01, above g_spine03's all g_spine03). Null otherwise.</summary>
        (int, float)[]? SpineRamp(int bone, Vector3 p)
        {
            string n = m.Bones[bone].Name;
            if (!(n.Equals("Bip001 Spine", StringComparison.OrdinalIgnoreCase) || n.Equals("Bip001 Spine1", StringComparison.OrdinalIgnoreCase)) || Mff("Bip001 Spine2") >= 0) return null;
            int pel = Mff("Bip001 Pelvis"), neck = Mff("Bip001 Neck");
            int[] hs = [sk.Find("g_spine01"), sk.Find("g_spine02"), sk.Find("g_spine03")];
            int hp = sk.Find("g_pelvis"), hn = sk.Find("g_neck");
            if (pel < 0 || neck < 0 || hp < 0 || hn < 0 || hs.Any(h => h < 0)) return null;
            float hSpan = sk.Pos(hn).Z - sk.Pos(hp).Z, mSpan = m.Bones[neck].Position.Z - m.Bones[pel].Position.Z;
            if (hSpan <= 1e-3f || mSpan <= 1e-3f) return null;
            var f = hs.Select(h => (sk.Pos(h).Z - sk.Pos(hp).Z) / hSpan).ToArray();
            float t = (p.Z - m.Bones[pel].Position.Z) / mSpan;
            if (t <= f[0]) return [(hs[0], 1f)];
            if (t >= f[2]) return [(hs[2], 1f)];
            int k = t < f[1] ? 0 : 1;
            float u = (t - f[k]) / Math.Max(1e-4f, f[k + 1] - f[k]);
            return [(hs[k], 1 - u), (hs[k + 1], u)];
        }

        /// <summary>For the MFF pelvis's weight at <paramref name="p"/> (MFF frame): the MHO bone of the thigh on the vertex's
        /// side and the share it takes (0 at the hip joint, 1 at a fifth of the way down the thigh, smooth). Null for other bones.</summary>
        (int, float)? HipShare(int bone, Vector3 p)
        {
            if (!m.Bones[bone].Name.Equals("Bip001 Pelvis", StringComparison.OrdinalIgnoreCase)) return null;
            int tl = Mff("Bip001 L Thigh"), tr = Mff("Bip001 R Thigh"), cl = Mff("Bip001 L Calf"), cr = Mff("Bip001 R Calf");
            if (tl < 0 || tr < 0 || cl < 0 || cr < 0) return null;
            bool left = MathF.Abs(p.Y - m.Bones[tl].Position.Y) < MathF.Abs(p.Y - m.Bones[tr].Position.Y);
            int t = left ? tl : tr, c = left ? cl : cr;
            var seg = m.Bones[c].Position - m.Bones[t].Position; float len2 = seg.LengthSquared();
            if (len2 < 1e-6f) return null;
            float u = Math.Clamp(Vector3.Dot(p - m.Bones[t].Position, seg) / len2 / 0.2f, 0, 1);
            return (target[t], u * u * (3 - 2 * u));
        }

        /// <summary>The 4 largest weights, normalized (the game's limit per vertex).</summary>
        static (int, float)[] Top4(Dictionary<int, float> acc)
        {
            var top = acc.OrderByDescending(x => x.Value).Take(4).ToList();
            float sum = top.Sum(x => x.Value);
            return top.Select(x => (x.Key, x.Value / sum)).ToArray();
        }

        void FinalNotes()
        {
            // Hand check (MFF_DEBUG): the new skeleton's hand direction (wrist → middle knuckle) and knuckle line (index →
            // pinky) against the MHO bind hand's, in degrees.
            if (Environment.GetEnvironmentVariable("MFF_DEBUG") == "1")
                foreach (var ms in new[] { "l", "r" })
                {
                    int w = sk.Find($"g_{ms}_wrist"), mid = sk.Find($"g_{ms}_birdy1"), ix = sk.Find($"g_{ms}_index1"), pk = sk.Find($"g_{ms}_pinky1");
                    if (w < 0 || mid < 0 || ix < 0 || pk < 0) continue;
                    Vector3 P(int i) => r.Bones[i].Position;
                    float Deg(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1, 1)) * 180 / MathF.PI;
                    string side = ms == "l" ? "L" : "R";
                    int fh = Mff($"Bip001 {side} Hand"), ff = Mff($"Bip001 {side} Forearm"), ft = Mff($"Bip001 {side} ForeTwist");
                    if (fh >= 0 && ff >= 0)
                    {
                        var rel = Quaternion.Normalize(delta[fh] * Quaternion.Inverse(delta[ff]));
                        float relDeg = 2 * MathF.Acos(Math.Clamp(MathF.Abs(rel.W), 0, 1)) * 180 / MathF.PI;
                        var axis = Vector3.Normalize(m.Bones[fh].Position - m.Bones[ff].Position);
                        var twistPart = new Vector3(rel.X, rel.Y, rel.Z); float along = Vector3.Dot(twistPart, Vector3.Transform(axis, delta[ff]));
                        float twistDeg = 2 * MathF.Atan2(MathF.Abs(along), MathF.Abs(rel.W)) * 180 / MathF.PI;
                        r.Notes.Add($"hand vs forearm {ms}: {relDeg:0.0} deg apart, {twistDeg:0.0} deg of it a twist about the forearm; ForeTwist bone {(ft >= 0 ? $"at {Vector3.Dot(m.Bones[ft].Position - m.Bones[ff].Position, m.Bones[fh].Position - m.Bones[ff].Position) / (m.Bones[fh].Position - m.Bones[ff].Position).LengthSquared():P0} of elbow to wrist" : "none")}");
                    }
                    // Mesh bend: the centroid of the vertices the hand bone mostly drives, wrist → centroid against elbow → wrist.
                    {
                        int el = sk.Find($"g_{ms}_elbow");
                        Vector3 sum = Vector3.Zero; int cnt = 0;
                        foreach (var s in r.Sections) for (int v = 0; v < s.Pos.Length; v++) if (s.Weights[v].Length > 0 && s.Weights[v].MaxBy(x => x.Weight).Bone == w) { sum += s.Pos[v]; cnt++; }
                        Vector3 osum = Vector3.Zero; int ocnt = 0;
                        if (fh >= 0) foreach (var p in m.Parts) foreach (var s in p.Sections) for (int v = 0; v < s.Pos.Length; v++) if (s.Weights[v].Length > 0 && s.Weights[v].MaxBy(x => x.Weight).Bone == fh) { osum += s.Pos[v]; ocnt++; }
                        if (cnt > 0 && el >= 0 && ocnt > 0 && ff >= 0)
                            r.Notes.Add($"hand mesh bend {ms}: result {Deg(sum / cnt - P(w), P(w) - P(el)):0.0} deg ({cnt} verts), MFF {Deg(osum / ocnt - m.Bones[fh].Position, m.Bones[fh].Position - m.Bones[ff].Position):0.0} deg ({ocnt} verts), MHO bones {Deg(sk.Pos(mid) - sk.Pos(w), sk.Pos(w) - sk.Pos(el)):0.0} deg");
                    }
                    int th = sk.Find($"g_{ms}_thumb1");
                    if (th >= 0) r.Notes.Add($"hand check {ms} (MFF-driven bones only): index1 to birdy1 off {Deg(P(mid) - P(ix), sk.Pos(mid) - sk.Pos(ix)):0.0} deg, wrist to thumb1 off {Deg(P(th) - P(w), sk.Pos(th) - sk.Pos(w)):0.0} deg");
                    r.Notes.Add($"hand check {ms}: direction off {Deg(P(mid) - P(w), sk.Pos(mid) - sk.Pos(w)):0.0} deg, knuckle line off {Deg(P(ix) - P(pk), sk.Pos(ix) - sk.Pos(pk)):0.0} deg"
                        + $" (mapped: index1 {r.Bones[ix].Mapped} from {r.Bones[ix].From}, pinky1 {r.Bones[pk].Mapped} from {r.Bones[pk].From}, birdy1 from {r.Bones[mid].From})");
                }
            var unmapped = m.Bones.Where(b => b.Deforms && !r.Map.ContainsKey(b.Name)).Select(b => b.Name).ToList();
            if (unmapped.Count > 0) r.Notes.Add($"{unmapped.Count} weighted MFF bone(s) without an MHO bone give their weights to the nearest mapped parent: {string.Join(", ", unmapped.Take(12))}{(unmapped.Count > 12 ? " …" : "")}");
            var missing = new[] { "g_pelvis", "g_head", "g_l_wrist", "g_r_wrist", "g_l_ankle", "g_r_ankle" }.Where(b => !mffOf.ContainsKey(b)).ToList();
            if (missing.Count > 0) r.Notes.Add("MHO bones with no MFF bone: " + string.Join(", ", missing));
        }
    }
}
