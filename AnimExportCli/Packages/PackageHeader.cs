namespace AnimExportCli.Packages;

/// <summary>One block of the package that is stored compressed on disk.</summary>
public readonly record struct PackageChunk(int UncompressedOffset, int UncompressedSize, int CompressedOffset, int CompressedSize);

[Flags]
public enum PackageCompression
{
    None = 0,
    Zlib = 1,
    Lzo = 2,
    LzoEncrypted = 4,
}

/// <summary>
/// The fixed-layout header every cooked UE3-family package starts with.
/// </summary>
/// <remarks>
/// Field order matches the standard UE3 package header used across the whole
/// engine generation — table offsets/counts, a package GUID, a generation
/// list, then a compression flag and chunk table. Nothing here is specific to
/// any one game; the chunk-compression tail is the standard "cooked, LZO- or
/// zlib-packed" scheme most UE3 titles ship with.
/// </remarks>
public sealed record PackageHeader
{
    public const uint Magic = 0x9E2A83C1;

    public required int FileVersion { get; init; }
    public required int LicenseeVersion { get; init; }
    public required int TotalHeaderSize { get; init; }
    public required string FolderName { get; init; }
    public required uint PackageFlags { get; init; }

    public required int NameCount { get; init; }
    public required int NameOffset { get; init; }
    public required int ExportCount { get; init; }
    public required int ExportOffset { get; init; }
    public required int ImportCount { get; init; }
    public required int ImportOffset { get; init; }
    public required int DependsOffset { get; init; }

    public required int ImportExportGuidsOffset { get; init; }
    public required int ImportGuidsCount { get; init; }
    public required int ExportGuidsCount { get; init; }
    public required int ThumbnailTableOffset { get; init; }

    public required Guid PackageGuid { get; init; }
    public required int GenerationCount { get; init; }
    public required int EngineVersion { get; init; }
    public required int CookerVersion { get; init; }

    public required PackageCompression Compression { get; init; }
    public required IReadOnlyList<PackageChunk> Chunks { get; init; }

    public bool IsCompressed => Compression != PackageCompression.None && Chunks.Count > 0;

    public static PackageHeader Read(ReadOnlySpan<byte> package)
    {
        var cursor = new PackageCursor(package);

        uint magic = cursor.ReadUInt32("magic");
        if (magic != Magic)
            throw new InvalidPackageException($"Not a cooked package: expected magic 0x{Magic:X8}, found 0x{magic:X8}.");

        int fileVersion = cursor.ReadInt16("file version");
        int licenseeVersion = cursor.ReadInt16("licensee version");
        int totalHeaderSize = cursor.ReadInt32("total header size");
        string folderName = cursor.ReadString("folder name");
        uint packageFlags = cursor.ReadUInt32("package flags");

        int nameCount = cursor.ReadInt32("name count");
        int nameOffset = cursor.ReadInt32("name offset");
        int exportCount = cursor.ReadInt32("export count");
        int exportOffset = cursor.ReadInt32("export offset");
        int importCount = cursor.ReadInt32("import count");
        int importOffset = cursor.ReadInt32("import offset");
        int dependsOffset = cursor.ReadInt32("depends offset");

        int importExportGuidsOffset = cursor.ReadInt32("import/export guids offset");
        int importGuidsCount = cursor.ReadInt32("import guids count");
        int exportGuidsCount = cursor.ReadInt32("export guids count");
        int thumbnailTableOffset = cursor.ReadInt32("thumbnail table offset");

        Guid packageGuid = cursor.ReadGuid("package guid");

        int generationCount = cursor.ReadInt32("generation count");
        if (generationCount < 0) throw new InvalidPackageException($"Negative generation count {generationCount}.");
        cursor.Skip(generationCount * 4 * 3); // export/name/net-object counts per generation — not needed downstream

        int engineVersion = cursor.ReadInt32("engine version");
        int cookerVersion = cursor.ReadInt32("cooker version");

        var compression = (PackageCompression)cursor.ReadInt32("compression flags");

        int chunkCount = cursor.ReadInt32("compressed chunk count");
        if (chunkCount < 0) throw new InvalidPackageException($"Negative compressed chunk count {chunkCount}.");

        const int bytesPerChunk = 4 * 4;
        if ((long)chunkCount * bytesPerChunk > cursor.Remaining)
        {
            throw new InvalidPackageException(
                $"Compressed chunk count {chunkCount} needs {(long)chunkCount * bytesPerChunk} bytes but only " +
                $"{cursor.Remaining} remain — the header is corrupt.");
        }

        var chunks = new PackageChunk[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            chunks[i] = new PackageChunk(
                cursor.ReadInt32($"chunk {i} uncompressed offset"),
                cursor.ReadInt32($"chunk {i} uncompressed size"),
                cursor.ReadInt32($"chunk {i} compressed offset"),
                cursor.ReadInt32($"chunk {i} compressed size"));
        }

        return new PackageHeader
        {
            FileVersion = fileVersion,
            LicenseeVersion = licenseeVersion,
            TotalHeaderSize = totalHeaderSize,
            FolderName = folderName,
            PackageFlags = packageFlags,
            NameCount = nameCount,
            NameOffset = nameOffset,
            ExportCount = exportCount,
            ExportOffset = exportOffset,
            ImportCount = importCount,
            ImportOffset = importOffset,
            DependsOffset = dependsOffset,
            ImportExportGuidsOffset = importExportGuidsOffset,
            ImportGuidsCount = importGuidsCount,
            ExportGuidsCount = exportGuidsCount,
            ThumbnailTableOffset = thumbnailTableOffset,
            PackageGuid = packageGuid,
            GenerationCount = generationCount,
            EngineVersion = engineVersion,
            CookerVersion = cookerVersion,
            Compression = compression,
            Chunks = chunks,
        };
    }
}
