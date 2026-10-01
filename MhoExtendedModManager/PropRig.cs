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

    /// <summary>A prop the character can hold: its mesh, the bone it's held on, the weapon slots it fills and whether it
    /// only shows while a power shows it.</summary>
    public sealed record Prop(MeshRef Ref, string? Bone)
    {
        public IReadOnlyList<string> Slots { get; init; } = [];
        public bool OnDemand { get; init; }
        public string Class { get; init; } = "";
    }

    /// <summary>
    /// The props the game attaches to <paramref name="main"/>: the attachment classes its player class default lists
    /// (mAttachmentClasses), else its hero's (a costume class keeps the base package UC__MarvelPlayer_&lt;Hero&gt;_SF's list:
    /// Punisher TV has none of its own, the base names his 17 guns). Each class's default is found in the costume package
    /// (a costume's own weapon, e.g. thorhammer_ageofultron), else in the base package (the mod's copy, else the game's in
    /// <paramref name="cooked"/>). Props marked visible_on_demand show only while a power switches their slot in
    /// (SetSwitches). A package without a class list: its own attachments, all shown (Hercules Enhanced Costumes: never
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
                        list.Add(new Prop(mr, a.Bone) { Slots = a.Slots, OnDemand = a.OnDemand, Class = a.Class });
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
        if (cooked != null && File.Exists(Path.Combine(cooked, file))) return (file, Path.Combine(cooked, file));
        return null;
    }

    /// <summary>The bone a prop goes on: the one named, else the right hand (palm, hand or wrist, as the skeleton has it).</summary>
    public static int BoneFor(MeshAnimator animator, string? bone)
    {
        int b = bone != null ? animator.BoneIndex(bone) : -1;
        if (b < 0) foreach (string guess in new[] { "g_r_palm", "g_r_hand", "g_r_wrist", "r_hand", "righthand" }) if ((b = animator.BoneIndex(guess)) >= 0) break;
        return Math.Max(0, b);
    }

    public void Clear() { props.Clear(); visible = []; }
    public void Add(ModMeshes.Loaded mesh, int bone) => Add(mesh, bone, [], false, "");
    public void Add(ModMeshes.Loaded mesh, int bone, IReadOnlyList<string> slots, bool onDemand, string cls) { props.Add((mesh, bone, slots, onDemand, cls)); At(0); }

    /// <summary>Some prop's showing depends on the power playing (on demand).</summary>
    public bool Switchable => props.Any(p => p.OnDemand);

    List<ModMeshes.PropRule> rules = [];
    float contactSeconds, animSeconds;

    /// <summary>The playing power's prop rules (ModMeshes.PropRules), with its contact time and the animation's length for
    /// their timing. None = what the character holds when no power plays.</summary>
    public void SetRules(List<ModMeshes.PropRule> powerRules, float contact, float seconds)
    {
        rules = powerRules; contactSeconds = contact; animSeconds = seconds;
        At(0);
    }

    /// <summary>Kept for --anim-render callers: rules without timing.</summary>
    public void SetSwitches(IReadOnlyCollection<(string From, string To)> switches)
        => SetRules([.. switches.SelectMany(w => new[] { new ModMeshes.PropRule(false, w.From, "power_on_start", 0, null, 0), new ModMeshes.PropRule(true, w.To, "power_on_start", 0, null, 0) }).Where(r => r.Target.Length > 0)], 0, 1);

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
        int total = l.Positions.Length + props.Sum(p => p.Mesh.Positions.Length);
        pos = new Vector3[total]; nrm = new Vector3[total]; tan = new Vector4[total];
        if (props.Count == 0) return l;
        var p = new List<Vector3>(l.Positions); var n = new List<Vector3>(l.Normals); var t = new List<Vector4>(l.Tangents); var uvs = new List<Vector2>(l.Uv);
        var idx = new List<int>(l.Indices); var tri = new List<int>(l.TriangleSection); var looks = new List<Gui.ModelView.Look?>(l.Looks);
        foreach (var (m, _, _, _, _) in props)
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
        if (props.Count == 0) { view.UpdateGeometry(animator); return; }
        int count = animator.Positions.Length;
        if (pos.Length < count + props.Sum(x => x.Mesh.Positions.Length)) { view.UpdateGeometry(animator); return; }   // Combine not called yet
        Array.Copy(animator.Positions, pos, count); Array.Copy(animator.Normals, nrm, Math.Min(count, animator.Normals.Length)); Array.Copy(animator.Tangents, tan, Math.Min(count, animator.Tangents.Length));
        int at = count, pi = 0;
        foreach (var (m, bone, _, _, _) in props)
        {
            var mat = animator.BoneMatrix(bone);
            if (pi < visible.Length && !visible[pi++])
            {
                // Hidden (not this power's weapon): every vertex on one point, so its triangles have no area and aren't drawn.
                var c = mat.Translation;
                for (int v = 0; v < m.Positions.Length; v++, at++) pos[at] = c;
                continue;
            }
            for (int v = 0; v < m.Positions.Length; v++, at++)
            {
                pos[at] = Vector3.Transform(m.Positions[v], mat);
                if (v < m.Normals.Length) nrm[at] = Vector3.Normalize(Vector3.TransformNormal(m.Normals[v], mat));
                if (v < m.Tangents.Length) { var tv = m.Tangents[v]; var tt = Vector3.TransformNormal(new Vector3(tv.X, tv.Y, tv.Z), mat); tan[at] = new Vector4(tt.LengthSquared() > 0 ? Vector3.Normalize(tt) : tt, tv.W); }
            }
        }
        view.UpdateGeometry(pos, nrm, tan);
    }
}
