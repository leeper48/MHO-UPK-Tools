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
    readonly List<(ModMeshes.Loaded Mesh, int Bone)> props = [];
    Vector3[] pos = [], nrm = [];
    Vector4[] tan = [];

    public int Count => props.Count;

    /// <summary>The props the game attaches to <paramref name="main"/>: other meshes of its own package that an attachment in
    /// that package names, with the bone it names. Only its own package (Hercules Enhanced Costumes: one package's sword
    /// had landed on every Colossus costume of the mod and a pet), as Create from 3D ticks by default.</summary>
    public static List<(MeshRef Ref, string? Bone)> Attached(MeshRef main, IReadOnlyList<MeshRef> meshes)
    {
        var named = ModMeshes.Attachments(main.File).GroupBy(a => a.Mesh, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Bone, StringComparer.OrdinalIgnoreCase);
        return [.. meshes.Where(m => m.Key != main.Key && m.File.Equals(main.File, StringComparison.OrdinalIgnoreCase) && named.ContainsKey(m.Name)).Select(m => (m, named[m.Name]))];
    }

    /// <summary>The bone a prop goes on: the one named, else the right hand (palm, hand or wrist, as the skeleton has it).</summary>
    public static int BoneFor(MeshAnimator animator, string? bone)
    {
        int b = bone != null ? animator.BoneIndex(bone) : -1;
        if (b < 0) foreach (string guess in new[] { "g_r_palm", "g_r_hand", "g_r_wrist", "r_hand", "righthand" }) if ((b = animator.BoneIndex(guess)) >= 0) break;
        return Math.Max(0, b);
    }

    public void Clear() => props.Clear();
    public void Add(ModMeshes.Loaded mesh, int bone) => props.Add((mesh, bone));

    /// <summary>The character and the props as one mesh (props' sections after the character's); sizes the pose buffers.</summary>
    public ModMeshes.Loaded Combine(ModMeshes.Loaded l)
    {
        int total = l.Positions.Length + props.Sum(p => p.Mesh.Positions.Length);
        pos = new Vector3[total]; nrm = new Vector3[total]; tan = new Vector4[total];
        if (props.Count == 0) return l;
        var p = new List<Vector3>(l.Positions); var n = new List<Vector3>(l.Normals); var t = new List<Vector4>(l.Tangents); var uvs = new List<Vector2>(l.Uv);
        var idx = new List<int>(l.Indices); var tri = new List<int>(l.TriangleSection); var looks = new List<Gui.ModelView.Look?>(l.Looks);
        foreach (var (m, _) in props)
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
        int at = count;
        foreach (var (m, bone) in props)
        {
            var mat = animator.BoneMatrix(bone);
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
