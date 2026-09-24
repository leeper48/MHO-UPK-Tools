using AnimExportCli.Animation;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;
using AnimExportCli.Packages;

namespace AnimExportCli;

/// <summary>
/// The actual "find meshes, find matching animations, export" workflow,
/// pulled out of <see cref="Program"/> so the console entry point and the
/// GUI call the exact same code — nothing about what gets exported or how
/// it's named should ever differ between the two.
/// </summary>
public static class ExportWorkflow
{
    /// <summary>One skeletal mesh found in a package, with the counts that help tell meshes apart at a glance.</summary>
    public sealed class MeshEntry(SkeletalMesh mesh)
    {
        public SkeletalMesh Mesh { get; } = mesh;
        public string Name => Mesh.Name;
        public int BoneCount => Mesh.Bones.Count;
        public int VertexCount => Mesh.HighestDetail?.Positions.Count ?? 0;
        public int TriangleCount => Mesh.HighestDetail?.TriangleCount ?? 0;

        public override string ToString() => $"{Name}  —  {BoneCount} bones, {VertexCount} verts, {TriangleCount} tris";
    }

    /// <summary>One AnimSet found in a package, with the same "(N anims, M bones)" summary the reference tool shows.</summary>
    public sealed class AnimSetEntry(AnimObjectReader.AnimSetInfo info, string name)
    {
        public AnimObjectReader.AnimSetInfo Info { get; } = info;
        public string Name { get; } = name;
        public int SequenceCount => Info.Sequences.Count;
        public int BoneCount => Info.TrackBoneNames.Count;

        public override string ToString() => $"{Name} ({SequenceCount} anims, {BoneCount} bones)";
    }

    public readonly record struct ExportSummary(int Exported, int SkippedNoOverlap, int SkippedNoTracks, int SkippedExportFailed)
    {
        public int TotalSkipped => SkippedNoOverlap + SkippedNoTracks + SkippedExportFailed;
    }

    public static IReadOnlyList<MeshEntry> ListMeshes(Package package)
    {
        var meshes = new List<MeshEntry>();
        foreach (int index in package.FindExportsOfClass(SkeletalMeshReader.ClassName))
        {
            SkeletalMesh? mesh = SkeletalMeshReader.TryRead(package, index);
            if (mesh is not null) meshes.Add(new MeshEntry(mesh));
        }
        return meshes;
    }

    public static IReadOnlyList<AnimSetEntry> ListAnimSets(Package package)
    {
        var result = new List<AnimSetEntry>();
        foreach (AnimObjectReader.AnimSetInfo info in AnimObjectReader.FindAnimSets(package))
            result.Add(new AnimSetEntry(info, package.GetExportName(info.ExportIndex)));
        return result;
    }

    public static IReadOnlyList<string> ListSequenceNames(Package package, AnimObjectReader.AnimSetInfo animSet)
    {
        var names = new List<string>();
        foreach (ObjectReference sequenceRef in animSet.Sequences)
        {
            if (sequenceRef.IsExport) names.Add(AnimObjectReader.GetSequenceDisplayName(package, sequenceRef.ExportIndex));
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public static IReadOnlyList<string> ListAllSequenceNames(Package package, IEnumerable<AnimObjectReader.AnimSetInfo> animSets)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AnimObjectReader.AnimSetInfo animSet in animSets)
        {
            foreach (ObjectReference sequenceRef in animSet.Sequences)
            {
                if (sequenceRef.IsExport) names.Add(AnimObjectReader.GetSequenceDisplayName(package, sequenceRef.ExportIndex));
            }
        }

        var sorted = names.ToList();
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    public static string DefaultOutputDirectory(string upkPath, string meshName) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(upkPath)) ?? ".", Sanitize(meshName));

    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++) buffer[i] = invalid.Contains(name[i]) ? '_' : name[i];

        string cleaned = new(buffer);
        return cleaned.Length > 0 ? cleaned : "unnamed";
    }

    /// <summary>
    /// Writes the bind pose, then every animation from <paramref name="animSets"/>
    /// whose bones overlap <paramref name="mesh"/>'s — the same "does this set
    /// apply to this mesh" check regardless of whether the caller passed every
    /// AnimSet in the package or just one the user picked explicitly.
    /// </summary>
    public static ExportSummary Export(
        Package package, SkeletalMesh mesh, IReadOnlyList<AnimObjectReader.AnimSetInfo> animSets,
        string outputDirectory, string? nameFilter, Action<string> log, Action<string> logError)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(animSets);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logError);

        SkeletalMeshLod? lod = mesh.HighestDetail;
        if (lod is null || !lod.HasGeometry)
        {
            logError($"'{mesh.Name}' has no exportable geometry (no usable LOD).");
            return default;
        }

        Directory.CreateDirectory(outputDirectory);

        // Always write a plain, unanimated export alongside whatever animations
        // are found — useful on its own, and it's the file to compare against a
        // known-good export when something about a pose looks wrong, since it
        // isolates the mesh/skeleton reader from the animation baking entirely.
        string bindPosePath = Path.Combine(outputDirectory, "bindpose.fbx");
        try
        {
            FbxExporter.Write(bindPosePath, mesh, lod);
            log($"wrote {Path.GetFileName(bindPosePath)} (bind pose, no animation)");
        }
        catch (MeshExportException ex)
        {
            logError($"could not write bind pose: {ex.Message}");
        }

        var boneNames = new HashSet<string>(mesh.Bones.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        int exported = 0, skippedNoOverlap = 0, skippedNoTracks = 0, skippedExportFailed = 0;

        foreach (AnimObjectReader.AnimSetInfo animSet in animSets)
        {
            int overlap = animSet.TrackBoneNames.Count(n => n.Length > 0 && boneNames.Contains(n));
            if (overlap == 0)
            {
                skippedNoOverlap++;
                continue;
            }

            foreach (ObjectReference sequenceRef in animSet.Sequences)
            {
                if (!sequenceRef.IsExport) continue; // a sequence imported from another package isn't in this file to read

                if (!string.IsNullOrEmpty(nameFilter) &&
                    !AnimObjectReader.GetSequenceDisplayName(package, sequenceRef.ExportIndex).Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                BoneAnimation? animation;
                try
                {
                    animation = AnimObjectReader.TryRead(package, sequenceRef.ExportIndex, animSet.TrackBoneNames);
                }
                catch (InvalidPackageException ex)
                {
                    logError($"skipped export {sequenceRef.ExportIndex}: {ex.Message}");
                    skippedExportFailed++;
                    continue;
                }

                if (animation is null)
                {
                    skippedNoTracks++;
                    continue;
                }

                string fileName = $"{Sanitize(animation.Name)}.fbx";
                string outPath = Path.Combine(outputDirectory, fileName);

                try
                {
                    FbxExporter.WriteAnimated(outPath, mesh, lod, animation);
                    log($"wrote {fileName}");
                    exported++;
                }
                catch (MeshExportException ex)
                {
                    logError($"skipped {animation.Name}: {ex.Message}");
                    skippedExportFailed++;
                }
            }
        }

        return new ExportSummary(exported, skippedNoOverlap, skippedNoTracks, skippedExportFailed);
    }
}
