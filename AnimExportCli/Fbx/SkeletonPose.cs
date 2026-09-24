using System.Numerics;
using AnimExportCli.Meshes;

namespace AnimExportCli.Fbx;

/// <summary>Where every bone of a skeleton sits in the model's own space, at rest.</summary>
public sealed class SkeletonPose
{
    private SkeletonPose(IReadOnlyList<Matrix4x4> boneToModel, IReadOnlyList<Matrix4x4> modelToBone)
    {
        BoneToModel = boneToModel;
        ModelToBone = modelToBone;
    }

    public IReadOnlyList<Matrix4x4> BoneToModel { get; }
    public IReadOnlyList<Matrix4x4> ModelToBone { get; }

    /// <summary>
    /// Walks the parent chain once. Bones are stored parent-before-child, so
    /// one forward pass is enough; a bone whose parent comes after it in the
    /// list is left standing alone rather than built from an unready parent.
    /// </summary>
    public static SkeletonPose Rest(IReadOnlyList<MeshBone> bones)
    {
        var boneToModel = new Matrix4x4[bones.Count];
        var modelToBone = new Matrix4x4[bones.Count];

        for (int i = 0; i < bones.Count; i++)
        {
            MeshBone bone = bones[i];
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(Normalised(bone.Orientation)) * Matrix4x4.CreateTranslation(bone.Position);

            int parent = bone.ParentIndex;
            boneToModel[i] = parent >= 0 && parent < i ? local * boneToModel[parent] : local;
            modelToBone[i] = Matrix4x4.Invert(boneToModel[i], out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
        }

        return new SkeletonPose(boneToModel, modelToBone);
    }

    /// <summary>Stored rotations can be very slightly off unit length; a matrix built from one scales the bone as well as turning it.</summary>
    private static Quaternion Normalised(Quaternion rotation)
    {
        float length = rotation.Length();
        return length > 0.0001f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }
}
