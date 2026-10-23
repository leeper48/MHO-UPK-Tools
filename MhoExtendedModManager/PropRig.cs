using AnimExportCli.Animation;
using System.Numerics;

namespace MhoExtendedModManager;

/// <summary>
/// Props held by a character in a 3D view (Kurt: a character holding a sword or hammer; 2026-09-30: in the main preview
/// too, not only Create from 3D). A prop is another skeletal mesh of the character's package(s) that a marvelattachment
/// default names (ModelMesh + AttachmentBones: Thor's hammer on g_r_palm). It has its own skeleton and is shown rigidly,
/// in its bind pose, placed by the holding bone's posed matrix with no offset (Thor's hammer sits right; the MHO Hero
/// Creator adds a socket only when a weapon moves to a hero whose hand bone hangs from another parent, which doesn't
/// happen for a mod's own costume). The character and its props are one mesh in the view (props' sections after the
/// character's); each frame the character is skinned and the props follow their bones.
/// </summary>
sealed class PropRig
{
    readonly List<(ModMeshes.Loaded Mesh, int Bone, IReadOnlyList<string> Slots, bool OnDemand, string Class)> props = [];
    bool[] visible = [];
    Vector3[] pos = [], nrm = [];
    Vector4[] tan = [];

    public int Count => props.Count;
    /// <summary>The props' classes (tests).</summary>
    public IEnumerable<string> Classes => props.Select(p => p.Class);

    /// <summary>The character and its props as last posed (Update): the box around them, or null before the first pose.</summary>
    public (Vector3 Lo, Vector3 Hi)? Bounds()
    {
        if (pos.Length == 0 || props.Count + actors.Count == 0) return null;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in pos) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        return lo.X <= hi.X ? (lo, hi) : null;
    }

    /// <summary>A prop the character can hold: its mesh, the bone it's held on, the weapon slots it fills and whether it
    /// only shows while a power shows it.</summary>
    public sealed record Prop(MeshRef Ref, string? Bone)
    {
        public IReadOnlyList<string> Slots { get; init; } = [];
        public bool OnDemand { get; init; }
        public string Class { get; init; } = "";
        public bool UseParentAnim { get; init; }
        /// <summary>The attachment's OffsetRotation as a matrix (identity when none).</summary>
        public Matrix4x4 Offset { get; init; } = Matrix4x4.Identity;
    }

    /// <summary>
    /// The props the game attaches to <paramref name="main"/>: the attachment classes its player class default lists
    /// (mAttachmentClasses), else its hero's (a costume class keeps the base package UC__MarvelPlayer_&lt;Hero&gt;_SF's list:
    /// Punisher TV has none of its own, the base names his 17 guns). Each class's default is found in the costume package
    /// (a costume's own weapon, e.g. thorhammer_ageofultron), else in the base package (the mod's copy, else the game's in
    /// <paramref name="cooked"/>). Props marked visible_on_demand show only while a power switches their slot in
    /// (SetRules). A package without a class list: its own attachments, all shown (Hercules Enhanced Costumes: never
    /// another package's, one package's sword had landed on every Colossus costume of the mod and a pet).
    /// </summary>
    public static List<Prop> Attached(MeshRef main, IReadOnlyList<MeshRef> meshes, string? cooked = null)
    {
        var cmp = StringComparison.OrdinalIgnoreCase;
        var basePkg = BasePackage(main, meshes, cooked);
        var ownMeshes = meshes.Where(m => m.Key != main.Key && m.File.Equals(main.File, cmp)).ToList();
        var classes = ModMeshes.AttachmentClasses(main.File) ?? (basePkg is { } b0 ? ModMeshes.AttachmentClasses(b0.Path) : null);
        if (classes is { Count: > 0 })
        {
            var ownAtt = ModMeshes.Attachments(main.File, basePkg?.Path);
            var baseAtt = basePkg is { } b1 ? ModMeshes.Attachments(b1.Path) : [];
            var candidates = basePkg is { } b2 ? [.. ownMeshes, .. ModMeshes.List([b2])] : ownMeshes;
            var list = new List<Prop>();
            foreach (string cls in classes.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var atts = ownAtt.Where(a => a.Class.Equals(cls, cmp)).ToList();
                if (atts.Count == 0) atts = [.. baseAtt.Where(a => a.Class.Equals(cls, cmp))];
                foreach (var a in atts)
                    if (candidates.FirstOrDefault(m => m.Name.Equals(a.Mesh, cmp)) is { } mr)
                        list.Add(new Prop(mr, a.Bone) { Slots = a.Slots, OnDemand = a.OnDemand, Class = a.Class, UseParentAnim = a.UseParentAnim,
                            Offset = ModMeshes.Rotator(a.OffsetRotation.Pitch, a.OffsetRotation.Yaw, a.OffsetRotation.Roll) });
            }
            if (list.Count > 0) return list;
        }
        var named = ModMeshes.Attachments(main.File).GroupBy(a => a.Mesh, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Bone, StringComparer.OrdinalIgnoreCase);
        return [.. ownMeshes.Where(m => named.ContainsKey(m.Name)).Select(m => new Prop(m, named[m.Name]))];
    }

    /// <summary>The hero's base package for a costume package (UC__MarvelPlayer_&lt;Hero&gt;_&lt;Costume&gt;_SF → UC__MarvelPlayer_&lt;Hero&gt;_SF):
    /// the mod's copy when it has one, else the game's; null for anything else.</summary>
    static (string File, string Path)? BasePackage(MeshRef main, IReadOnlyList<MeshRef> meshes, string? cooked)
    {
        var parts = Path.GetFileNameWithoutExtension(main.Package).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase)) return null;
        string file = $"UC__MarvelPlayer_{parts[2]}_SF.upk";
        string own = Path.Combine(Path.GetDirectoryName(main.File) ?? "", file);
        if (File.Exists(own)) return (file, own);
        if (cooked != null && File.Exists(Path.Combine(cooked, file))) return (file, StockFiles.For(cooked, file));
        return null;
    }

    /// <summary>The bone a prop goes on: the one named, else the right hand (palm, hand or wrist, as the skeleton has it).</summary>
    public static int BoneFor(MeshAnimator animator, string? bone)
    {
        int b = bone != null ? animator.BoneIndex(bone) : -1;
        // a bone the attachment names that the skeleton doesn't have: the character's origin, where a ride's own animation
        // places it (Hawkeye's Sky-Cycle names g_mountattach, which no player mesh has, his own included; on the right
        // hand it sat on Thor's shoulder, Kurt 2026-10-20)
        if (b < 0 && !string.IsNullOrEmpty(bone) && !bone.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;
        if (b < 0) foreach (string guess in new[] { "g_r_palm", "g_r_hand", "g_r_wrist", "r_hand", "righthand" }) if ((b = animator.BoneIndex(guess)) >= 0) break;
        return Math.Max(0, b);
    }

    public void Clear() { props.Clear(); motion.Clear(); offsets.Clear(); visible = []; actors.Clear(); sockets.Clear(); }

    // Each prop's own sockets with a rest-pose animator for a prop that has none (Ghost Rider's Charge Up Bike plays its wheel
    // spin at the bike's socket_rear_wheel: at the character's origin before, Kurt 2026-10-12)
    readonly List<(Dictionary<string, (string Bone, Matrix4x4 Local)> Map, MeshAnimator? RestPose)> sockets = [];
    static readonly Dictionary<string, Dictionary<string, (string Bone, Matrix4x4 Local)>> socketCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A prop mesh's sockets (Fx.FxSockets), read once per mesh.</summary>
    public static Dictionary<string, (string Bone, Matrix4x4 Local)> SocketsOf(MeshRef r)
    {
        string key = r.File + "|" + r.Name;
        lock (socketCache)
            if (socketCache.TryGetValue(key, out var d)) return d;
        var read = Fx.FxSockets.Of(r.File, r.Name);
        lock (socketCache) socketCache[key] = read;
        return read;
    }

    /// <summary>A shown prop's socket in the pose last shown (mesh transform, offset, bone; an animated prop's own pose), or null.</summary>
    public Matrix4x4? Socket(string name, MeshAnimator animator)
    {
        for (int i = 0; i < props.Count && i < sockets.Count; i++)
        {
            if (i < visible.Length && !visible[i]) continue;
            var (map, rest) = sockets[i];
            if (!map.TryGetValue(name, out var s)) continue;
            var mo = motion[i];
            var an = mo != null && (mo.Seq != null || mo.ParentAnim) ? mo.Anim : rest;
            if (an == null) continue;
            int b = an.BoneIndex(s.Bone);
            if (b < 0) continue;
            var (m, bone, _, _, _) = props[i];
            var mat = m.MeshTransform * offsets[i] * (bone < 0 ? Matrix4x4.Identity : animator.BoneMatrix(bone));
            return s.Local * an.BoneMatrix(b) * mat;
        }
        return null;
    }
    readonly List<Matrix4x4> offsets = [];

    /// <summary>
    /// An animated actor of the playing power (PowerEffects.Actor; Kurt, 2026-10-03): its own mesh and skeleton, placed in
    /// the hero's frame (<see cref="Place"/>: scale, turn, offset, at the hero or at the target), shown from
    /// <see cref="Start"/> to <see cref="End"/> seconds into the animation and playing its own animation from its start.
    /// </summary>
    sealed class ActorItem
    { public bool Steady;
        public ModMeshes.Loaded Mesh = null!;
        public MeshAnimator Anim = null!;
        public BoneAnimation? Seq;
        public float Frames, Seconds, Start, End;
        public Matrix4x4 Place;
    }
    readonly List<ActorItem> actors = [];

    /// <summary>The playing power's colour change (the Powers tab's colour, PowerColor.Apply), for the actors' glowing
    /// sections; null: the game's colours.</summary>
    public Func<Vector3, Vector3>? Recolor { get; set; }

    /// <param name="steady">A summon (Jean Grey's illusion): its see-through material doesn't fade out as an afterimage's does.</param>
    public void AddActor(ModMeshes.Loaded mesh, MeshAnimator anim, BoneAnimation? seq, float start, float end, Matrix4x4 place, bool steady = false)
    {
        var (frames, seconds) = seq == null ? (0f, 0f) : MeshAnimator.Span(seq);
        actors.Add(new ActorItem { Mesh = mesh, Anim = anim, Seq = seq, Frames = frames, Seconds = seconds, Start = start, End = end, Place = mesh.MeshTransform * place, Steady = steady });
    }
    public void Add(ModMeshes.Loaded mesh, int bone) => Add(mesh, bone, [], false, "");
    public void Add(ModMeshes.Loaded mesh, int bone, IReadOnlyList<string> slots, bool onDemand, string cls, MeshAnimator? anim = null, List<AnimRef>? anims = null, bool parentAnim = false, Matrix4x4? offset = null,
        Dictionary<string, (string Bone, Matrix4x4 Local)>? propSockets = null)
    {
        props.Add((mesh, bone, slots, onDemand, cls));
        MeshAnimator? rest = null;
        if (propSockets is { Count: > 0 } && mesh.Bones.Count > 0)
        {
            rest = new MeshAnimator(mesh.Bones, mesh.Positions, mesh.Normals, mesh.Influences, mesh.Tangents);
            rest.Pose(null, 0);
        }
        sockets.Add((propSockets ?? new(), rest));
        offsets.Add(offset ?? Matrix4x4.Identity);
        motion.Add(anim != null ? new Motion { Anim = anim, Anims = anims ?? [], ParentAnim = parentAnim, Seq = parentAnim ? parentSeq : null, Frames = parentAnim && parentSeq != null ? MeshAnimator.Span(parentSeq).Frames : 0 } : null);
        At(0);
    }

    BoneAnimation? parentSeq;

    /// <summary>Every shown prop's frame as last posed (as Update places it): Look ▾ → Prop Pivots draws them.</summary>
    public List<(Matrix4x4 Frame, string Label)> ShownFrames(MeshAnimator animator)
    {
        var list = new List<(Matrix4x4, string)>();
        for (int i = 0; i < props.Count; i++)
        {
            if (i < visible.Length && !visible[i]) continue;
            int bone = props[i].Bone;
            list.Add((props[i].Mesh.MeshTransform * (i < offsets.Count ? offsets[i] : Matrix4x4.Identity) * (bone < 0 ? Matrix4x4.Identity : animator.BoneMatrix(bone)),
                      bone < 0 ? "on the character's skeleton" : "on " + animator.BoneName(bone)));
        }
        return list;
    }

    /// <summary>Where a prop of class <paramref name="cls"/> is held as last posed: its own transform, the attachment's offset,
    /// then its bone (as Update places it), or null when the rig has none. Prop Fit's global axes read it.</summary>
    public Matrix4x4? Frame(string cls, MeshAnimator animator)
    {
        for (int i = 0; i < props.Count; i++)
        {
            if (!props[i].Class.Equals(cls, StringComparison.OrdinalIgnoreCase)) continue;
            int bone = props[i].Bone;
            return props[i].Mesh.MeshTransform * (i < offsets.Count ? offsets[i] : Matrix4x4.Identity) * (bone < 0 ? Matrix4x4.Identity : animator.BoneMatrix(bone));
        }
        return null;
    }

    /// <summary>The character's animation, for props rigged to its skeleton (UseParentAnim): they're posed with it, bone by
    /// bone name; with none they show in their bind pose, which is already in the character's hands.</summary>
    public void SetParentAnimation(BoneAnimation? seq)
    {
        parentSeq = seq;
        foreach (var m in motion)
            if (m is { ParentAnim: true }) { m.Seq = seq; m.Frames = seq == null ? 0 : MeshAnimator.Span(seq).Frames; }
        At(lastSeconds);
    }

    /// <summary>
    /// An animated prop (MarvelAttachmentAnimated: whips, nunchucks, cables, chains) has its own skeleton and plays its own
    /// animation of the character's animation's name, at the same moment (Taskmaster's nunchucks in Nunchuck Beatdown:
    /// absattack_daredevil_triplestrike_01 in both). Held on its bone (often the character's root). Without an animation
    /// for the one playing it isn't shown: its bind pose is not how it's ever seen.
    /// </summary>
    sealed class Motion
    {
        public MeshAnimator Anim = null!;
        public List<AnimRef> Anims = [];
        public BoneAnimation? Seq;
        public float Frames;
        public bool ParentAnim;
    }
    readonly List<Motion?> motion = [];
    double lastSeconds;

    /// <summary>For the animation <paramref name="anim"/>: each animated prop's own animation of that name (to load).</summary>
    public List<(string Class, AnimRef Ref)> MotionRefs(string? anim)
    {
        var list = new List<(string, AnimRef)>();
        if (anim == null) return list;
        for (int i = 0; i < props.Count; i++)
            if (motion[i] is { } m && m.Anims.FirstOrDefault(a => a.Name.Equals(anim, StringComparison.OrdinalIgnoreCase)) is { } r) list.Add((props[i].Class, r));
        return list;
    }

    /// <summary>The loaded animations of the animated props, by class (none = they don't show).</summary>
    public void SetMotions(IReadOnlyDictionary<string, BoneAnimation> seqs)
    {
        for (int i = 0; i < props.Count; i++)
            if (motion[i] is { ParentAnim: false } m)   // (props on the character's skeleton keep the character's animation)
            {
                m.Seq = seqs.TryGetValue(props[i].Class, out var s) ? s : null;
                m.Frames = m.Seq == null ? 0 : MeshAnimator.Span(m.Seq).Frames;
            }
        At(lastSeconds);
    }

    List<ModMeshes.PropRule> rules = [];
    float contactSeconds, animSeconds;

    /// <summary>The playing power's prop rules (ModMeshes.PropRules), with its contact time and the animation's length for
    /// their timing. None = what the character holds when no power plays.</summary>
    public void SetRules(List<ModMeshes.PropRule> powerRules, float contact, float seconds)
    {
        rules = powerRules; contactSeconds = contact; animSeconds = seconds;
        At(0);
    }

    float PointTime(string point, float offset)
    {
        float t = point.Contains("end", StringComparison.OrdinalIgnoreCase) ? animSeconds
            : point.Contains("start", StringComparison.OrdinalIgnoreCase) || point.Length == 0 ? 0 : contactSeconds;   // contact, target result …
        return t + offset;
    }

    /// <summary>Which props show at <paramref name="seconds"/> into the animation: the always-held ones, then each rule that
    /// has started (and, for a window, not ended) in start order. A slot "bothhands" means the left and right hands.</summary>
    public void At(double seconds)
    {
        lastSeconds = seconds;
        if (visible.Length != props.Count) visible = new bool[props.Count];
        for (int i = 0; i < props.Count; i++) visible[i] = !props[i].OnDemand;
        foreach (var r in rules.OrderBy(r => PointTime(r.StartPoint, r.StartOffset)))
        {
            float st = PointTime(r.StartPoint, r.StartOffset);
            if (seconds + 1e-4 < st) continue;
            if (r.EndPoint != null && seconds >= PointTime(r.EndPoint, r.EndOffset) && PointTime(r.EndPoint, r.EndOffset) > st) continue;
            for (int i = 0; i < props.Count; i++)
                if (Matches(props[i], r.Target)) visible[i] = r.Show;
        }
        for (int i = 0; i < props.Count; i++) if (motion.Count > i && motion[i] is { Seq: null, ParentAnim: false }) visible[i] = false;
    }

    static bool Matches((ModMeshes.Loaded Mesh, int Bone, IReadOnlyList<string> Slots, bool OnDemand, string Class) p, string target) => Fills(p.Slots, p.Class, target);

    /// <summary>Whether a prop is what a rule's target names: its class ("class:…"), one of its slots, or "bothhands"
    /// for a prop in either hand.</summary>
    public static bool Fills(Prop p, string target) => Fills(p.Slots, p.Class, target);

    static bool Fills(IReadOnlyList<string> slots, string cls, string target)
    {
        if (target.StartsWith("class:", StringComparison.OrdinalIgnoreCase)) return cls.Equals(target[6..], StringComparison.OrdinalIgnoreCase);
        if (slots.Contains(target, StringComparer.OrdinalIgnoreCase)) return true;
        return target.Equals("bothhands", StringComparison.OrdinalIgnoreCase) && (slots.Contains("lefthand", StringComparer.OrdinalIgnoreCase) || slots.Contains("righthand", StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The character and the props as one mesh (props' sections after the character's); sizes the pose buffers.</summary>
    public ModMeshes.Loaded Combine(ModMeshes.Loaded l)
    {
        int total = l.Positions.Length + props.Sum(p => p.Mesh.Positions.Length) + actors.Sum(a => a.Mesh.Positions.Length);
        pos = new Vector3[total]; nrm = new Vector3[total]; tan = new Vector4[total];
        if (props.Count == 0 && actors.Count == 0) return l;
        var p = new List<Vector3>(l.Positions); var n = new List<Vector3>(l.Normals); var t = new List<Vector4>(l.Tangents); var uvs = new List<Vector2>(l.Uv);
        var idx = new List<int>(l.Indices); var tri = new List<int>(l.TriangleSection); var looks = new List<Gui.ModelView.Look?>(l.Looks);
        foreach (var m in props.Select(x => x.Mesh).Concat(actors.Select(a => a.Mesh)))
        {
            int vbase = p.Count, sbase = looks.Count;
            p.AddRange(m.Positions); n.AddRange(m.Normals); t.AddRange(m.Tangents); uvs.AddRange(m.Uv);
            idx.AddRange(m.Indices.Select(i => i + vbase)); tri.AddRange(m.TriangleSection.Select(s => s + sbase)); looks.AddRange(m.Looks);
        }
        return l with { Positions = [.. p], Normals = [.. n], Tangents = [.. t], Uv = [.. uvs], Indices = [.. idx], TriangleSection = [.. tri], Looks = [.. looks] };
    }

    /// <summary>Shows the pose the animator was last posed in: the character skinned, each prop moved by its bone.</summary>
    public void Update(Gui.ModelView view, MeshAnimator animator)
    {
        var forward = view.ViewForward;
        if (props.Count == 0 && actors.Count == 0) { view.UpdateGeometry(animator); return; }
        int count = animator.Positions.Length;
        if (pos.Length < count + props.Sum(x => x.Mesh.Positions.Length) + actors.Sum(a => a.Mesh.Positions.Length)) { view.UpdateGeometry(animator); return; }   // Combine not called yet
        Array.Copy(animator.Positions, pos, count); Array.Copy(animator.Normals, nrm, Math.Min(count, animator.Normals.Length)); Array.Copy(animator.Tangents, tan, Math.Min(count, animator.Tangents.Length));
        int at = count, pi = 0;
        var ghost = new List<Gui.ModelView.FxTri>();
        foreach (var (m, bone, _, _, _) in props)
        {
            // (bone -1: rigged to the character's skeleton and posed in its model space already; the root bone's matrix
            // turned Taskmaster's bow a second time, onto the wrong side)
            // The mesh's own RotOrigin, the attachment's OffsetRotation, then the bone (UE3's order).
            var mat = m.MeshTransform * (pi < offsets.Count ? offsets[pi] : Matrix4x4.Identity) * (bone < 0 ? Matrix4x4.Identity : animator.BoneMatrix(bone));
            var mo = pi < motion.Count ? motion[pi] : null;
            if (pi < visible.Length && !visible[pi++])
            {
                // Hidden (not this power's weapon): every vertex on one point, so its triangles have no area and aren't drawn.
                var c = mat.Translation;
                for (int v = 0; v < m.Positions.Length; v++, at++) pos[at] = c;
                continue;
            }
            // An animated prop: its own animation posed at the character's moment, then held on its bone.
            Vector3[] sp = m.Positions, sn = m.Normals; Vector4[] st = m.Tangents;
            if (mo != null && (mo.Seq != null || mo.ParentAnim))
            {
                // (on the character's skeleton: its posed bones, as the game copies them; else its own animation)
                if (mo.ParentAnim) mo.Anim.PoseFrom(animator);
                else mo.Anim.Pose(mo.Seq, mo.Seq != null && animSeconds > 0 ? (float)(Math.Clamp(lastSeconds / animSeconds, 0, 1) * mo.Frames) : 0);
                sp = mo.Anim.Positions; sn = mo.Anim.Normals; st = mo.Anim.Tangents;
            }
            int propAt = at;
            for (int v = 0; v < m.Positions.Length; v++, at++)
            {
                pos[at] = Vector3.Transform(sp[v], mat);
                if (v < sn.Length) nrm[at] = Vector3.Normalize(Vector3.TransformNormal(sn[v], mat));
                if (v < st.Length) { var tv = st[v]; var tt = Vector3.TransformNormal(new Vector3(tv.X, tv.Y, tv.Z), mat); tan[at] = new Vector4(tt.LengthSquared() > 0 ? Vector3.Normalize(tt) : tt, tv.W); }
            }
            // Its effect-material sections (Jean Grey's Phoenix wings, unlit and see-through: the view skips them as surfaces) as
            // effect triangles with their own texture, glowing when additive; opacity divided by the view's effect strength.
            for (int k = 0; k + 2 < m.Indices.Length; k += 3)
            {
                int sec = k / 3 < m.TriangleSection.Length ? m.TriangleSection[k / 3] : 0;
                if (sec >= m.Looks.Length || m.Looks[sec] is not { Ghost: true } gl) continue;
                var orig = gl.GhostMap != null ? Vector3.One : gl.GhostColor;
                var col = Recolor?.Invoke(orig) ?? orig;
                float mx = MathF.Max(col.X, MathF.Max(col.Y, col.Z));
                var tint = mx > 1 ? col / mx : col;
                int a0 = m.Indices[k], a1 = m.Indices[k + 1], a2 = m.Indices[k + 2];
                Vector2 U(int i) => i < m.Uv.Length ? m.Uv[i] : default;
                ghost.Add(new Gui.ModelView.FxTri(pos[propAt + a0], pos[propAt + a1], pos[propAt + a2], U(a0), U(a1), U(a2),
                    new Vector4(tint, 0.9f / Math.Max(0.01f, PreviewViews.FxPower)), gl.GhostMap, gl.GhostAdditive, false));
            }
        }
        foreach (var a in actors)
        {
            var m = a.Mesh;
            if (lastSeconds < a.Start || lastSeconds > a.End)
            {
                var c = a.Place.Translation;
                for (int v = 0; v < m.Positions.Length; v++, at++) pos[at] = c;
                continue;
            }
            int baseAt = at;
            float t = (float)(lastSeconds - a.Start);
            a.Anim.Pose(a.Seq, a.Seq != null && a.Seconds > 0 ? Math.Clamp(t / a.Seconds, 0, 1) * a.Frames : 0);
            Vector3[] sp = a.Anim.Positions, sn = a.Anim.Normals; Vector4[] st = a.Anim.Tangents;
            for (int v = 0; v < m.Positions.Length; v++, at++)
            {
                pos[at] = Vector3.Transform(sp[v], a.Place);
                if (v < sn.Length) nrm[at] = Vector3.Normalize(Vector3.TransformNormal(sn[v], a.Place));
                if (v < st.Length) { var tv = st[v]; var tt = Vector3.TransformNormal(new Vector3(tv.X, tv.Y, tv.Z), a.Place); tan[at] = new Vector4(tt.LengthSquared() > 0 ? Vector3.Normalize(tt) : tt, tv.W); }
            }
            // Its effect-material sections (silhouettes) as glowing triangles: the view skips them as surfaces. Their opacity
            // is divided by the preview's effect strength, which the view multiplies every effect by (10 %).
            // (an HDR colour scaled into range; an afterimage fading out over its life, from 35 % opacity)
            float life = Math.Max(0.05f, a.End - a.Start), fade = a.Steady ? 0.35f : 0.35f * (1 - Math.Clamp(t / life, 0, 1) * 0.85f);
            Vector3 tint = Vector3.Zero;
            float bright = 1;
            for (int k = 0; k + 2 < m.Indices.Length; k += 3)
            {
                int sec = k / 3 < m.TriangleSection.Length ? m.TriangleSection[k / 3] : 0;
                if (sec >= m.Looks.Length || m.Looks[sec] is not { Ghost: true } gl) continue;
                // Through the power's colour change, as its particles: a glow shows as bright as its colour is, so a darker
                // colour makes it fainter and black hides it (Kurt, 2026-10-03).
                var orig = gl.GhostColor;
                var col = Recolor?.Invoke(orig) ?? orig;
                float mo = MathF.Max(orig.X, MathF.Max(orig.Y, orig.Z)), mx = MathF.Max(col.X, MathF.Max(col.Y, col.Z));
                bright = mo > 1e-6f ? Math.Clamp(mx / mo, 0, 1) : 1;
                tint = mx > 1 ? col / mx : col;
                int i0 = baseAt + m.Indices[k], i1 = baseAt + m.Indices[k + 1], i2 = baseAt + m.Indices[k + 2];
                if (gl.GhostInner is { } inner && gl.GhostOuter is { } outer)
                {
                    // a fresnel glow (Jean Grey's illusions): the material's inner colour where the surface faces the camera,
                    // its outer colour at the edges, as the game's mat_jg_illusion_jean draws them
                    var n = Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]);
                    float facing = n.LengthSquared() > 1e-12f ? MathF.Abs(Vector3.Dot(Vector3.Normalize(n), forward)) : 1;
                    float edge = MathF.Pow(1 - facing, 2f / gl.GhostEdge);
                    var c = Vector3.Lerp(new Vector3(inner.X, inner.Y, inner.Z), new Vector3(outer.X, outer.Y, outer.Z), edge);
                    c = Recolor?.Invoke(c) ?? c;
                    float cm = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
                    float alpha = Math.Clamp(inner.W + (Math.Min(1f, outer.W * 0.45f) - inner.W) * edge, 0, 1) * (a.Steady ? 1 : fade / 0.35f);
                    ghost.Add(new Gui.ModelView.FxTri(pos[i0], pos[i1], pos[i2], default, default, default,
                        new Vector4(cm > 1 ? c / cm : c, alpha / Math.Max(0.01f, PreviewViews.FxPower)), null, gl.GhostAdditive, false));
                    continue;
                }
                ghost.Add(new Gui.ModelView.FxTri(pos[i0], pos[i1], pos[i2], default, default, default,
                    new Vector4(tint, fade * bright / Math.Max(0.01f, PreviewViews.FxPower)), null, gl.GhostAdditive, false));
            }
        }
        if (ghost.Count > 0) view.EffectTris = [.. view.EffectTris, .. ghost];
        view.UpdateGeometry(pos, nrm, tan);
    }
}
