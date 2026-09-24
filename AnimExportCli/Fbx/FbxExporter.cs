using System.Numerics;
using Assimp;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using AssimpMesh = Assimp.Mesh;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion; // Assimp also defines Quaternion; this keeps every bare use unambiguous.

namespace AnimExportCli.Fbx;

public sealed class MeshExportException(string message) : Exception(message);

/// <summary>
/// Writes a skinned model — and optionally one baked animation clip — to an
/// FBX, DAE, or OBJ file via AssimpNet.
/// </summary>
public static class FbxExporter
{
    public static IReadOnlyList<string> Extensions { get; } = [".fbx", ".dae", ".obj"];

    public static void Write(string path, SkeletalMesh mesh, SkeletalMeshLod lod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Scene scene = BuildScene(mesh, lod, out _);
        Save(scene, path);
    }

    /// <summary>
    /// Writes the model with one baked animation clip attached. A bone the
    /// clip doesn't drive keeps its bind pose for the whole clip.
    /// </summary>
    public static void WriteAnimated(string path, SkeletalMesh mesh, SkeletalMeshLod lod, BoneAnimation animation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(animation);

        Scene scene = BuildScene(mesh, lod, out SkeletonPose rest);

        Assimp.Animation baked = BuildAnimation(mesh, animation, rest);
        if (baked.NodeAnimationChannels.Count == 0)
            throw new MeshExportException($"None of '{animation.Name}''s tracks match a bone in '{mesh.Name}' — nothing to bake.");

        scene.Animations.Add(baked);
        Save(scene, path);
    }

    private static Scene BuildScene(SkeletalMesh mesh, SkeletalMeshLod lod, out SkeletonPose rest)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(lod);
        if (!lod.HasGeometry) throw new MeshExportException("This level of detail holds no geometry.");

        var scene = new Scene { RootNode = new Node("Scene") };
        rest = SkeletonPose.Rest(mesh.Bones);
        BuildSkeleton(scene, mesh, rest);

        IReadOnlyList<MeshSection> sections = lod.Sections.Count > 0
            ? lod.Sections
            : [new MeshSection { MaterialIndex = 0, BaseIndex = 0, TriangleCount = lod.TriangleCount }];

        for (int s = 0; s < sections.Count; s++)
        {
            AssimpMesh? part = BuildSection(mesh, lod, sections[s], rest, s);
            if (part is null) continue;

            part.MaterialIndex = scene.MaterialCount;
            scene.Materials.Add(new Material { Name = $"material{sections[s].MaterialIndex}" });
            scene.Meshes.Add(part);
            scene.RootNode.MeshIndices.Add(scene.MeshCount - 1);
        }

        if (scene.MeshCount == 0) throw new MeshExportException("This model has no triangles to write.");
        return scene;
    }

    private static Node[] BuildSkeleton(Scene scene, SkeletalMesh mesh, SkeletonPose rest)
    {
        var nodes = new Node[mesh.Bones.Count];
        for (int b = 0; b < mesh.Bones.Count; b++) nodes[b] = new Node(mesh.Bones[b].Name);

        for (int b = 0; b < mesh.Bones.Count; b++)
        {
            int parent = mesh.Bones[b].ParentIndex;
            bool isRoot = b == 0 || parent < 0 || parent == b || parent >= mesh.Bones.Count;

            Matrix4x4 local = isRoot ? rest.BoneToModel[b] : rest.BoneToModel[b] * rest.ModelToBone[parent];
            nodes[b].Transform = ToAssimp(ToFileSpace(local));

            if (isRoot) scene.RootNode.Children.Add(nodes[b]);
            else nodes[parent].Children.Add(nodes[b]);
        }

        return nodes;
    }

    private static AssimpMesh? BuildSection(SkeletalMesh mesh, SkeletalMeshLod lod, MeshSection section, SkeletonPose rest, int number)
    {
        int firstCorner = section.BaseIndex;
        int corners = section.TriangleCount * 3;
        if (firstCorner < 0 || corners <= 0 || firstCorner + corners > lod.Indices.Count) return null;

        var used = new Dictionary<int, int>();
        var order = new List<int>();
        for (int c = firstCorner; c < firstCorner + corners; c++)
        {
            int vertex = lod.Indices[c];
            if (used.TryAdd(vertex, order.Count)) order.Add(vertex);
        }

        var part = new AssimpMesh($"part{number}", PrimitiveType.Triangle);

        foreach (int v in order)
        {
            Vector3 position = ToFileSpace(lod.Positions[v]);
            Vector3 normal = ToFileSpace(v < lod.Normals.Count ? lod.Normals[v] : Vector3.UnitZ);

            part.Vertices.Add(new Vector3D(position.X, position.Y, position.Z));
            part.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));

            Vector2 uv = v < lod.TexCoords.Count ? lod.TexCoords[v] : Vector2.Zero;
            part.TextureCoordinateChannels[0].Add(new Vector3D(uv.X, 1f - uv.Y, 0f));
        }

        part.UVComponentCount[0] = 2;

        // Left wound as read: swapping the two upright axes already flips
        // every triangle once, and reading a round-tripped model back in
        // flips it again — reversing here as well would send it out inside out.
        for (int c = firstCorner; c + 2 < firstCorner + corners; c += 3)
            part.Faces.Add(new Face([used[lod.Indices[c]], used[lod.Indices[c + 1]], used[lod.Indices[c + 2]]]));

        AddSkinning(part, mesh, lod, order, rest);
        return part;
    }

    private static void AddSkinning(AssimpMesh part, SkeletalMesh mesh, SkeletalMeshLod lod, List<int> order, SkeletonPose rest)
    {
        var byBone = new Dictionary<int, Bone>();

        for (int i = 0; i < order.Count; i++)
        {
            int v = order[i];
            if (v >= lod.Influences.Count) continue;

            VertexInfluence influence = lod.Influences[v];
            for (int w = 0; w < influence.Bones.Count; w++)
            {
                int bone = influence.Bones[w];
                float weight = influence.Weights[w];
                if (weight <= 0f || bone < 0 || bone >= mesh.Bones.Count) continue;

                if (!byBone.TryGetValue(bone, out Bone? entry))
                {
                    entry = new Bone { Name = mesh.Bones[bone].Name, OffsetMatrix = ToAssimp(ToFileSpace(rest.ModelToBone[bone])) };
                    byBone[bone] = entry;
                }

                entry.VertexWeights.Add(new VertexWeight(i, weight));
            }
        }

        // Every bone gets an entry, even one nothing is weighted to — a
        // modelling tool tells bones from plain nodes by whether the skin
        // reaches them.
        for (int b = 0; b < mesh.Bones.Count; b++)
        {
            if (byBone.ContainsKey(b)) continue;
            byBone[b] = new Bone { Name = mesh.Bones[b].Name, OffsetMatrix = ToAssimp(ToFileSpace(rest.ModelToBone[b])) };
        }

        foreach (Bone bone in byBone.OrderBy(p => p.Key).Select(p => p.Value)) part.Bones.Add(bone);
    }

    /// <summary>
    /// AssimpNet's FBX exporter does not honour a custom <c>TicksPerSecond</c>
    /// on the <see cref="Assimp.Animation"/> object — whatever value is set
    /// there, every key time handed to it comes out in the file multiplied by
    /// a fixed 24. Confirmed by comparing this tool's output against a
    /// known-good FBX from another tool for the same clip: dividing by this
    /// constant before handing times to Assimp reproduces that file's key
    /// times exactly (both use frame-number ticks, not real seconds).
    /// </summary>
    private const float AssimpFbxTickRate = 24f;

    /// <summary>
    /// Bakes one clip's tracks into an Assimp animation, one channel per bone
    /// it drives. Position and rotation keys can sit at different frames —
    /// every distinct frame is sampled (linear for position, spherical for
    /// rotation) so every output key is one full local transform, converted
    /// through the same <see cref="ToFileSpace(Matrix4x4)"/> the bind pose
    /// uses. Key times are frame numbers (see <see cref="BonePositionKey"/>),
    /// scaled by <see cref="AssimpFbxTickRate"/> only right here, at the
    /// boundary with Assimp.
    /// </summary>
    private static Assimp.Animation BuildAnimation(SkeletalMesh mesh, BoneAnimation animation, SkeletonPose rest)
    {
        // TicksPerSecond deliberately isn't 1.0 despite that being the
        // semantically "honest" value for the tick units used below (see
        // AssimpFbxTickRate) — confirmed by round-trip testing that at 1.0,
        // AssimpNet's FBX writer silently truncates every channel at
        // exactly tick 1.0, regardless of DurationInTicks, dropping any
        // key past what amounts to one real second. A large value pushes
        // that apparent one-second boundary far outside any real clip's
        // range without changing anything about how individual key times
        // are computed — this tool's own ÷24 scaling and the writer's own
        // ×24 reinterpretation of it are untouched either way.
        var baked = new Assimp.Animation { Name = animation.Name, TicksPerSecond = 1000.0 };
        float maxFrame = 0f;

        for (int b = 0; b < mesh.Bones.Count; b++)
        {
            MeshBone bone = mesh.Bones[b];
            if (!animation.Tracks.TryGetValue(bone.Name, out BoneTrack? track)) continue;
            if (track.PositionKeys.Count == 0 && track.RotationKeys.Count == 0) continue;

            Vector3 restPosition = bone.Position;
            Quaternion restRotation = NormalisedOrIdentity(bone.Orientation);

            // A single rotation key is usually a structural, near-constant
            // bone (root, the various "_offset" bones) rather than genuine
            // per-frame animation — and those were never separately checked
            // when the engine's conjugate rotation convention was verified
            // (that verification used real multi-key limb motion, compared
            // frame-by-frame against a known-good export). Reconstructing a
            // rotation from just X/Y/Z leaves its sign fundamentally
            // ambiguous, so rather than trust the same fixed convention
            // blindly here too, pick whichever of the two possible readings
            // — as decoded, or its conjugate — sits closer to this bone's
            // own bind pose, which is already proven correct. A bone that's
            // genuinely meant to hold a real non-bind-pose orientation for
            // the whole clip is unaffected: both readings are equally
            // "real" rotations, this just picks the one actually intended.
            // A single rotation key is usually a structural, near-constant
            // bone (root, the various "_offset" bones) rather than genuine
            // per-frame animation — see the remarks on ResolveSingleKeyRotation.
            IReadOnlyList<BoneRotationKey> rotationKeys = track.RotationKeys;
            if (rotationKeys.Count == 1)
                rotationKeys = [new BoneRotationKey(rotationKeys[0].TimeFrame, ResolveSingleKeyRotation(rotationKeys[0].Rotation, restRotation))];

            var frames = new SortedSet<float>();
            foreach (BonePositionKey key in track.PositionKeys) frames.Add(key.TimeFrame);
            foreach (BoneRotationKey key in rotationKeys) frames.Add(key.TimeFrame);
            if (frames.Count == 0) continue;

            var channel = new NodeAnimationChannel { NodeName = bone.Name };

            foreach (float frame in frames)
            {
                Vector3 position = SamplePosition(track.PositionKeys, frame, restPosition);
                Quaternion rotation = SampleRotation(rotationKeys, frame, restRotation);

                Matrix4x4 local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                Matrix4x4.Decompose(ToFileSpace(local), out _, out Quaternion outRotation, out Vector3 outPosition);

                float tick = frame / AssimpFbxTickRate;
                channel.PositionKeys.Add(new VectorKey(tick, new Vector3D(outPosition.X, outPosition.Y, outPosition.Z)));
                channel.RotationKeys.Add(new QuaternionKey(tick, ToAssimp(outRotation)));
            }

            if (frames.Max > maxFrame) maxFrame = frames.Max;
            baked.NodeAnimationChannels.Add(channel);
        }

        baked.DurationInTicks = maxFrame / AssimpFbxTickRate;
        return baked;
    }

    internal static Vector3 SamplePosition(IReadOnlyList<BonePositionKey> keys, float frame, Vector3 fallback)
    {
        if (keys.Count == 0) return fallback;
        if (keys.Count == 1 || frame <= keys[0].TimeFrame) return keys[0].Position;
        if (frame >= keys[^1].TimeFrame) return keys[^1].Position;

        for (int i = 1; i < keys.Count; i++)
        {
            if (frame > keys[i].TimeFrame) continue;
            BonePositionKey a = keys[i - 1], c = keys[i];
            float span = c.TimeFrame - a.TimeFrame;
            float t = span > 0f ? (frame - a.TimeFrame) / span : 0f;
            return Vector3.Lerp(a.Position, c.Position, t);
        }

        return keys[^1].Position;
    }

    internal static Quaternion SampleRotation(IReadOnlyList<BoneRotationKey> keys, float frame, Quaternion fallback)
    {
        if (keys.Count == 0) return fallback;
        if (keys.Count == 1 || frame <= keys[0].TimeFrame) return keys[0].Rotation;
        if (frame >= keys[^1].TimeFrame) return keys[^1].Rotation;

        for (int i = 1; i < keys.Count; i++)
        {
            if (frame > keys[i].TimeFrame) continue;
            BoneRotationKey a = keys[i - 1], c = keys[i];
            float span = c.TimeFrame - a.TimeFrame;
            float t = span > 0f ? (frame - a.TimeFrame) / span : 0f;
            return Quaternion.Slerp(a.Rotation, c.Rotation, t);
        }

        return keys[^1].Rotation;
    }

    /// <summary>
    /// Chooses between a decoded single-key rotation and its conjugate by
    /// picking whichever sits closer to the bone's own bind pose.
    /// </summary>
    /// <remarks>
    /// A single rotation key is usually a structural, near-constant bone
    /// (root, the various "_offset" bones) rather than genuine per-frame
    /// animation — and those were never separately checked when the engine's
    /// conjugate rotation convention was verified (that verification used
    /// real multi-key limb motion, compared frame-by-frame against a
    /// known-good export). Reconstructing a rotation from just X/Y/Z leaves
    /// its sign fundamentally ambiguous, so rather than trust the same fixed
    /// convention blindly here too, this picks whichever of the two possible
    /// readings sits closer to this bone's own bind pose, which is already
    /// proven correct. A bone that's genuinely meant to hold a real
    /// non-bind-pose orientation for the whole clip is unaffected: both
    /// readings are equally "real" rotations, this just picks the one
    /// actually intended.
    /// <para>
    /// Exposed rather than private so <see cref="RoundTripVerifier"/> can
    /// apply the exact same correction to the baseline it compares against —
    /// otherwise it would flag every bone this legitimately corrects as
    /// "drifted", when the round trip is doing precisely what it's meant to.
    /// </para>
    /// </remarks>
    internal static Quaternion ResolveSingleKeyRotation(Quaternion decoded, Quaternion restRotation)
    {
        var conjugated = new Quaternion(-decoded.X, -decoded.Y, -decoded.Z, decoded.W);
        float decodedSimilarity = MathF.Abs(Quaternion.Dot(decoded, restRotation));
        float conjugatedSimilarity = MathF.Abs(Quaternion.Dot(conjugated, restRotation));
        return conjugatedSimilarity > decodedSimilarity ? conjugated : decoded;
    }

    internal static Quaternion NormalisedOrIdentity(Quaternion rotation)
    {
        float length = rotation.Length();
        return length > 0.0001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }

    private static void Save(Scene scene, string path)
    {
        string format = Path.GetExtension(path).ToLowerInvariant() switch { ".dae" => "collada", ".obj" => "obj", _ => "fbx" };

        using var exporter = new AssimpContext();
        try
        {
            if (!exporter.ExportFile(scene, path, format))
                throw new MeshExportException($"The model could not be written as {format}.");
        }
        catch (AssimpException ex)
        {
            throw new MeshExportException($"The model could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// The engine and the file use different axis conventions; this swap is
    /// its own inverse (swapping the same two axes back undoes it exactly),
    /// which is what lets <see cref="FbxAnimationImporter"/> reuse it
    /// unchanged to convert a baked file-space animation back to engine
    /// space, rather than needing a separate inverse implementation that
    /// could drift out of sync with this one.
    /// </summary>
    internal static Vector3 ToFileSpace(Vector3 value) => new(value.X, value.Z, value.Y);

    internal static Matrix4x4 ToFileSpace(Matrix4x4 m) => new(
        m.M11, m.M13, m.M12, m.M14,
        m.M31, m.M33, m.M32, m.M34,
        m.M21, m.M23, m.M22, m.M24,
        m.M41, m.M43, m.M42, m.M44);

    private static Assimp.Matrix4x4 ToAssimp(Matrix4x4 m) => new(
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44);

    private static Assimp.Quaternion ToAssimp(Quaternion q) => new(q.W, q.X, q.Y, q.Z);
}
