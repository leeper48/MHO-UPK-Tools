using System.Numerics;
using AnimExportCli.Packages;
using AnimExportCli.Packages.Properties;

namespace AnimExportCli.Meshes;

/// <summary>One bone of a skeleton, naming its parent by index.</summary>
public sealed record MeshBone
{
    public required string Name { get; init; }
    public required int ParentIndex { get; init; }

    /// <summary>Rest orientation, relative to the parent.</summary>
    public required Quaternion Orientation { get; init; }

    /// <summary>Rest position, relative to the parent.</summary>
    public required Vector3 Position { get; init; }
}

/// <summary>A run of triangles drawn with one material.</summary>
public sealed record MeshSection
{
    public required int MaterialIndex { get; init; }
    public required int BaseIndex { get; init; }
    public required int TriangleCount { get; init; }
}

/// <summary>
/// A run of vertices that share one set of bones. A vertex names its bones by
/// a number local to its own chunk (a small number takes less room than an
/// index into the whole skeleton); the bone map turns that local number back
/// into a skeleton index.
/// </summary>
public sealed record MeshChunk
{
    public required int BaseVertexIndex { get; init; }
    public required int VertexCount { get; init; }
    public required IReadOnlyList<int> BoneMap { get; init; }

    public bool Covers(int vertexIndex) => vertexIndex >= BaseVertexIndex && vertexIndex < BaseVertexIndex + VertexCount;
}

/// <summary>Which bones a vertex follows, and how strongly (weights sum to one).</summary>
public readonly record struct VertexInfluence
{
    public required IReadOnlyList<int> Bones { get; init; }
    public required IReadOnlyList<float> Weights { get; init; }
}

/// <summary>How a vertex buffer stores its position and texture coordinates.</summary>
public readonly record struct VertexLayout(bool PackedPosition, bool FullPrecisionUvs, int UvSetCount)
{
    public const int TangentFrameBytes = 8;
    private const int SkinningBytes = 8; // four bone indices, four weights
    public const int FixedBytes = TangentFrameBytes + SkinningBytes;
    public int PositionOffset => FixedBytes;
}

/// <summary>One level of detail: geometry drawn as a set of sections.</summary>
public sealed record SkeletalMeshLod
{
    public required IReadOnlyList<MeshSection> Sections { get; init; }
    public required IReadOnlyList<int> Indices { get; init; }
    public required IReadOnlyList<Vector3> Positions { get; init; }
    public required IReadOnlyList<Vector3> Normals { get; init; }
    public required IReadOnlyList<Vector2> TexCoords { get; init; }
    public required IReadOnlyList<VertexInfluence> Influences { get; init; }
    public required IReadOnlyList<MeshChunk> Chunks { get; init; }

    public int TriangleCount => Indices.Count / 3;
    public bool HasGeometry => Positions.Count > 0;
}

/// <summary>A skinned model: a skeleton plus one or more levels of detail.</summary>
public sealed record SkeletalMesh
{
    public required string Name { get; init; }
    public required IReadOnlyList<MeshBone> Bones { get; init; }
    public required IReadOnlyList<SkeletalMeshLod> Lods { get; init; }

    public SkeletalMeshLod? HighestDetail => Lods.Count > 0 ? Lods[0] : null;

    public override string ToString() => $"{Name} ({Bones.Count} bones, {Lods.Count} LOD(s))";
}

/// <summary>
/// Reads skinned models out of a SkeletalMesh export.
/// </summary>
/// <remarks>
/// The vertex buffer has no single fixed layout: positions are either
/// quantised into four bytes or stored as three floats, texture coordinates
/// are either half or full precision, and there can be one to four coordinate
/// sets. The layout is worked out from the element size the file itself
/// declares for its vertex buffer, rather than assumed from the layout flags
/// alone (a flag can say "packed" on a buffer that plainly isn't).
/// <para>
/// This binary layout was established by comparing real exported
/// packages byte-for-byte, not from any public specification — treat every
/// offset and constant below as an empirically reverse-engineered fact about
/// this game's cook, not a general UE3 fact.
/// </para>
/// </remarks>
public static class SkeletalMeshReader
{
    public const string ClassName = "skeletalmesh";

    private const int BoneBytes = 52;
    private const int MaxBones = 4096;
    private const int MaxLods = 16;
    private const int MaxSections = 1024;

    public static SkeletalMesh? TryRead(Package package, int exportIndex, Action<string>? onFailure = null)
    {
        if (!string.Equals(package.GetExportClassName(exportIndex), ClassName, StringComparison.OrdinalIgnoreCase))
            return null;

        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null)
        {
            onFailure?.Invoke("properties did not parse");
            return null;
        }

        try
        {
            return Read(package, exportIndex, properties);
        }
        catch (InvalidPackageException ex)
        {
            onFailure?.Invoke(ex.Message);
            return null;
        }
    }

    private static SkeletalMesh Read(Package package, int exportIndex, PropertyBag properties)
    {
        ReadOnlySpan<byte> data = package.GetExportData(exportIndex);
        var cursor = new PackageCursor(data, properties.PayloadOffset);

        cursor.Skip(MeshBounds.ByteSize);

        SkipObjectArray(ref cursor); // materials — this tool doesn't need them

        cursor.Skip(4 * 3); // origin
        cursor.Skip(4 * 3); // rotation origin

        IReadOnlyList<MeshBone> bones = ReadBones(ref cursor, package.Names);

        cursor.Skip(4); // skeletal depth

        // Whether a per-vertex colour buffer follows each level's vertex
        // buffer is a property, not anything in the binary payload — getting
        // it wrong shifts every level after the first.
        bool hasVertexColours = properties.GetBool("bhasvertexcolors");

        int lodCount = cursor.ReadInt32("LOD count");
        if (lodCount < 0 || lodCount > MaxLods) throw new InvalidPackageException($"Mesh declares {lodCount} levels of detail.");

        var lods = new List<SkeletalMeshLod>(lodCount);
        for (int i = 0; i < lodCount; i++) lods.Add(ReadLod(ref cursor, i, hasVertexColours));

        return new SkeletalMesh { Name = package.GetExportName(exportIndex), Bones = bones, Lods = lods };
    }

    private static void SkipObjectArray(ref PackageCursor cursor)
    {
        int count = cursor.ReadInt32("material count");
        if (count < 0 || (long)count * 4 > cursor.Remaining) throw new InvalidPackageException($"Mesh declares {count} materials.");
        cursor.Skip(count * 4);
    }

    private static IReadOnlyList<MeshBone> ReadBones(ref PackageCursor cursor, NameTable names)
    {
        int count = cursor.ReadInt32("bone count");
        if (count < 0 || count > MaxBones || (long)count * BoneBytes > cursor.Remaining)
            throw new InvalidPackageException($"Mesh declares {count} bones.");

        var bones = new MeshBone[count];

        for (int i = 0; i < count; i++)
        {
            int nameIndex = cursor.ReadInt32($"bone {i} name");
            int nameNumber = cursor.ReadInt32($"bone {i} name number");
            cursor.Skip(4); // flags

            var orientation = new Quaternion(
                cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
            var position = new Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());

            cursor.Skip(4); // child count — not needed; parent links are enough to rebuild the hierarchy
            int parentIndex = cursor.ReadInt32($"bone {i} parent");

            cursor.Skip(4); // bone colour

            bones[i] = new MeshBone
            {
                Name = nameIndex >= 0 && nameIndex < names.Count ? names.Resolve(nameIndex, nameNumber) : $"bone{i}",
                ParentIndex = parentIndex,
                Orientation = orientation,
                Position = position,
            };
        }

        return bones;
    }

    private static SkeletalMeshLod ReadLod(ref PackageCursor cursor, int lodIndex, bool hasVertexColours)
    {
        IReadOnlyList<MeshSection> sections = ReadSections(ref cursor, lodIndex);
        IReadOnlyList<int> indices = ReadIndexBuffer(ref cursor, lodIndex);

        SkipNumberArray(ref cursor, 2); // active bones — not needed once vertex bone indices are already skeleton-resolved
        IReadOnlyList<MeshChunk> chunks = ReadChunks(ref cursor, lodIndex);

        cursor.Skip(4); // declared size

        int vertexCount = (int)cursor.ReadUInt32($"LOD {lodIndex} vertex count");
        if (vertexCount < 0) throw new InvalidPackageException($"LOD {lodIndex} declares {vertexCount} vertices.");

        SkipNumberArray(ref cursor, 1); // required bones
        SkipBulkData(ref cursor);       // raw point indices

        int uvSets = (int)cursor.ReadUInt32($"LOD {lodIndex} texture coordinate sets");
        if (uvSets is < 1 or > 4) uvSets = 1;

        (IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<Vector2> texCoords, IReadOnlyList<VertexInfluence> influences) =
            ReadVertexBuffer(ref cursor, lodIndex, uvSets, chunks);

        // Nothing below is needed to draw this level, but the cursor still has
        // to cross it intact or the next level starts in the wrong place.
        if (hasVertexColours) SkipBulkArray(ref cursor);
        SkipVertexInfluenceSets(ref cursor, lodIndex);
        SkipIndexContainer(ref cursor);

        return new SkeletalMeshLod
        {
            Sections = sections,
            Indices = indices,
            Positions = positions,
            Normals = normals,
            TexCoords = texCoords,
            Influences = influences,
            Chunks = chunks,
        };
    }

    /// <summary>
    /// Each section entry is: material index (uint16), chunk index (uint16,
    /// unused here), base index (uint32), triangle count (uint32), and one
    /// trailing sort-mode byte.
    /// </summary>
    private static IReadOnlyList<MeshSection> ReadSections(ref PackageCursor cursor, int lodIndex)
    {
        const int sectionBytes = (2 * 2) + (4 * 2) + 1;

        int count = cursor.ReadInt32($"LOD {lodIndex} section count");
        if (count < 0 || count > MaxSections || (long)count * sectionBytes > cursor.Remaining)
            throw new InvalidPackageException($"LOD {lodIndex} declares {count} sections.");

        var sections = new MeshSection[count];
        for (int i = 0; i < count; i++)
        {
            int materialIndex = cursor.ReadUInt16();
            cursor.Skip(2); // chunk index — this reader locates chunks by vertex range instead
            int baseIndex = (int)cursor.ReadUInt32();
            int triangleCount = (int)cursor.ReadUInt32();
            cursor.Skip(1); // triangle sort mode

            sections[i] = new MeshSection { MaterialIndex = materialIndex, BaseIndex = baseIndex, TriangleCount = triangleCount };
        }

        return sections;
    }

    private static IReadOnlyList<int> ReadIndexBuffer(ref PackageCursor cursor, int lodIndex)
    {
        cursor.Skip(4); // "stays resident for the CPU" flag — not needed for export

        int elementWidth = cursor.ReadByte();
        int declaredWidth = cursor.ReadInt32($"LOD {lodIndex} index element size");
        int count = cursor.ReadInt32($"LOD {lodIndex} index count");

        if (elementWidth is not (2 or 4) || declaredWidth != elementWidth)
            throw new InvalidPackageException($"LOD {lodIndex} index buffer states widths {elementWidth} and {declaredWidth} that disagree.");
        if (count < 0 || (long)count * elementWidth > cursor.Remaining)
            throw new InvalidPackageException($"LOD {lodIndex} declares {count} indices.");

        var indices = new int[count];
        for (int i = 0; i < count; i++)
            indices[i] = elementWidth == 2 ? cursor.ReadUInt16() : cursor.ReadInt32();

        return indices;
    }

    private static (IReadOnlyList<Vector3> Positions, IReadOnlyList<Vector3> Normals, IReadOnlyList<Vector2> TexCoords, IReadOnlyList<VertexInfluence> Influences)
        ReadVertexBuffer(ref PackageCursor cursor, int lodIndex, int uvSets, IReadOnlyList<MeshChunk> chunks)
    {
        int repeatedUvSets = (int)cursor.ReadUInt32($"LOD {lodIndex} vertex buffer texture coordinate sets");
        if (repeatedUvSets is >= 1 and <= 4) uvSets = repeatedUvSets;

        bool fullPrecisionUvs = cursor.ReadUInt32() != 0;
        cursor.Skip(4); // packed-position flag — the element size below decides the layout, not this flag

        var extension = new Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
        var origin = new Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());

        int elementSize = cursor.ReadInt32($"LOD {lodIndex} vertex element size");
        int count = cursor.ReadInt32($"LOD {lodIndex} vertex count");

        if (count < 0 || elementSize <= 0 || (long)count * elementSize > cursor.Remaining)
            throw new InvalidPackageException($"LOD {lodIndex} declares {count} vertices of {elementSize} bytes.");

        int positionBytes = elementSize - VertexLayout.FixedBytes - (uvSets * (fullPrecisionUvs ? 8 : 4));
        if (positionBytes is not (4 or 12))
            throw new InvalidPackageException($"LOD {lodIndex} leaves {positionBytes} bytes for a position in a {elementSize}-byte vertex.");

        bool packedPosition = positionBytes == 4;
        var layout = new VertexLayout(packedPosition, fullPrecisionUvs, uvSets);

        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var texCoords = new Vector2[count];
        var influences = new VertexInfluence[count];

        int chunkAt = 0;
        MeshChunk? ChunkFor(int vertex)
        {
            while (chunkAt < chunks.Count - 1 && !chunks[chunkAt].Covers(vertex)) chunkAt++;
            return chunks.Count > 0 && chunks[chunkAt].Covers(vertex) ? chunks[chunkAt] : null;
        }

        int vertexStart = cursor.Position;
        int uvOffset = layout.PositionOffset + positionBytes;

        for (int i = 0; i < count; i++)
        {
            int vertex = vertexStart + (i * elementSize);
            int at = vertex + layout.PositionOffset;

            positions[i] = packedPosition
                ? UnpackPosition(cursor.PeekUInt32(at), origin, extension)
                : new Vector3(cursor.PeekSingle(at), cursor.PeekSingle(at + 4), cursor.PeekSingle(at + 8));

            // The tangent frame leads the vertex: a direction along the surface
            // first, then the surface normal — only the second is needed here.
            normals[i] = UnpackDirection(cursor.PeekUInt32(vertex + 4));

            influences[i] = ReadInfluence(ref cursor, vertex + VertexLayout.TangentFrameBytes, ChunkFor(i));

            texCoords[i] = fullPrecisionUvs
                ? new Vector2(cursor.PeekSingle(uvOffset + vertex), cursor.PeekSingle(uvOffset + vertex + 4))
                : new Vector2((float)cursor.PeekHalf(uvOffset + vertex), (float)cursor.PeekHalf(uvOffset + vertex + 2));
        }

        cursor.Skip(count * elementSize);
        return (positions, normals, texCoords, influences);
    }

    private static VertexInfluence ReadInfluence(ref PackageCursor cursor, int at, MeshChunk? chunk)
    {
        const int slots = 4;
        var bones = new List<int>(slots);
        var weights = new List<float>(slots);

        for (int i = 0; i < slots; i++)
        {
            int weight = cursor.PeekByte(at + slots + i);
            if (weight == 0) continue;

            int local = cursor.PeekByte(at + i);
            int bone = chunk is not null && local < chunk.BoneMap.Count ? chunk.BoneMap[local] : local;

            bones.Add(bone);
            weights.Add(weight / 255f);
        }

        float total = 0f;
        foreach (float w in weights) total += w;
        if (total > 0.0001f)
            for (int i = 0; i < weights.Count; i++) weights[i] /= total;

        return new VertexInfluence { Bones = bones, Weights = weights };
    }

    /// <summary>Unpacks a direction stored as four signed bytes (the fourth, handedness, is unused here).</summary>
    private static Vector3 UnpackDirection(uint packed)
    {
        var direction = new Vector3(
            ((packed & 0xFF) / 127.5f) - 1f,
            (((packed >> 8) & 0xFF) / 127.5f) - 1f,
            (((packed >> 16) & 0xFF) / 127.5f) - 1f);

        float length = direction.Length();
        return length > 1e-6f ? direction / length : Vector3.UnitZ;
    }

    /// <summary>Unpacks a quantised position: 11 bits X, 11 bits Y, 10 bits Z, each signed and scaled into the mesh's own range.</summary>
    private static Vector3 UnpackPosition(uint packed, Vector3 origin, Vector3 extension)
    {
        int x = (int)(packed & 0x7FF);
        int y = (int)((packed >> 11) & 0x7FF);
        int z = (int)((packed >> 22) & 0x3FF);

        if (x > 1023) x -= 2048;
        if (y > 1023) y -= 2048;
        if (z > 511) z -= 1024;

        return new Vector3(
            (x / 1023.0f * extension.X) + origin.X,
            (y / 1023.0f * extension.Y) + origin.Y,
            (z / 511.0f * extension.Z) + origin.Z);
    }

    private static void SkipNumberArray(ref PackageCursor cursor, int elementSize)
    {
        int count = cursor.ReadInt32("number array count");
        if (count < 0 || (long)count * elementSize > cursor.Remaining) throw new InvalidPackageException($"Array declares {count} entries.");
        cursor.Skip(count * elementSize);
    }

    private static void SkipBulkData(ref PackageCursor cursor)
    {
        const uint storedElsewhere = 0x01, noPayload = 0x20;

        uint flags = cursor.ReadUInt32();
        cursor.Skip(4); // element count
        int sizeOnDisk = cursor.ReadInt32();
        cursor.Skip(4); // offset

        if ((flags & (storedElsewhere | noPayload)) != 0) return;

        if (sizeOnDisk > 0)
        {
            if (sizeOnDisk > cursor.Remaining) throw new InvalidPackageException($"Bulk data block claims {sizeOnDisk} bytes.");
            cursor.Skip(sizeOnDisk);
        }
    }

    private static void SkipBulkArray(ref PackageCursor cursor)
    {
        int elementSize = cursor.ReadInt32();
        int count = cursor.ReadInt32();
        if (elementSize < 0 || count < 0 || (long)elementSize * count > cursor.Remaining)
            throw new InvalidPackageException($"Bulk array declares {count} entries of {elementSize} bytes.");
        cursor.Skip(elementSize * count);
    }

    private static void SkipIndexContainer(ref PackageCursor cursor)
    {
        cursor.Skip(4); // needs CPU access
        cursor.Skip(1); // index width
        SkipBulkArray(ref cursor);
    }

    /// <summary>
    /// Skips the alternative influence sets a model can carry for runtime part
    /// swaps. Each repeats the section and chunk lists, so this mirrors the
    /// same parsing rather than assuming a fixed size.
    /// </summary>
    private static void SkipVertexInfluenceSets(ref PackageCursor cursor, int lodIndex)
    {
        const int influenceBytes = 8;
        const int sectionBytes = (2 * 2) + (4 * 2) + 1;

        int count = cursor.ReadInt32($"LOD {lodIndex} influence set count");
        if (count < 0 || count > MaxSections) throw new InvalidPackageException($"LOD {lodIndex} declares {count} influence sets.");

        for (int i = 0; i < count; i++)
        {
            SkipArrayOf(ref cursor, influenceBytes);

            int mapCount = cursor.ReadInt32();
            if (mapCount < 0 || (long)mapCount * 12 > cursor.Remaining) throw new InvalidPackageException($"Influence map declares {mapCount} entries.");

            for (int m = 0; m < mapCount; m++)
            {
                cursor.Skip(8); // the pair of bones this entry keys on
                SkipArrayOf(ref cursor, 4);
            }

            SkipArrayOf(ref cursor, sectionBytes);
            ReadChunks(ref cursor, lodIndex);
            SkipArrayOf(ref cursor, 1);
            cursor.Skip(1); // what the set is used for
        }
    }

    private static void SkipArrayOf(ref PackageCursor cursor, int elementSize)
    {
        int count = cursor.ReadInt32();
        if (count < 0 || (long)count * elementSize > cursor.Remaining) throw new InvalidPackageException($"Array declares {count} entries.");
        cursor.Skip(count * elementSize);
    }

    /// <summary>
    /// Reads the chunk list, keeping each chunk's bone map — a vertex names
    /// the bones it follows by a number local to its chunk, and the bone map
    /// is what turns that into a position in the skeleton.
    /// </summary>
    private static IReadOnlyList<MeshChunk> ReadChunks(ref PackageCursor cursor, int lodIndex)
    {
        const int rigidVertexBytes = 61, softVertexBytes = 68;

        int count = cursor.ReadInt32($"LOD {lodIndex} chunk count");
        if (count < 0 || count > MaxSections) throw new InvalidPackageException($"LOD {lodIndex} declares {count} chunks.");

        var chunks = new List<MeshChunk>(count);

        for (int i = 0; i < count; i++)
        {
            int baseVertex = (int)cursor.ReadUInt32();

            SkipArrayOf(ref cursor, rigidVertexBytes);
            SkipArrayOf(ref cursor, softVertexBytes);

            int boneCount = cursor.ReadInt32();
            if (boneCount < 0 || (long)boneCount * 2 > cursor.Remaining) throw new InvalidPackageException($"Chunk {i} declares {boneCount} bones.");

            var boneMap = new int[boneCount];
            for (int b = 0; b < boneCount; b++) boneMap[b] = cursor.ReadUInt16();

            int rigid = cursor.ReadInt32();
            int soft = cursor.ReadInt32();
            cursor.Skip(4); // most bones any one vertex here follows — not needed

            chunks.Add(new MeshChunk
            {
                BaseVertexIndex = baseVertex,
                VertexCount = Math.Max(0, rigid) + Math.Max(0, soft),
                BoneMap = boneMap,
            });
        }

        return chunks;
    }
}
