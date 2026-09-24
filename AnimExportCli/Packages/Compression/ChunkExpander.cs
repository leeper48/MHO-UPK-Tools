namespace AnimExportCli.Packages.Compression;

/// <summary>One block inside a compressed chunk.</summary>
public readonly record struct ChunkBlock(int CompressedSize, int UncompressedSize);

/// <summary>
/// The small header that precedes a chunk's compressed bytes: a magic value
/// (packages nest their own magic here too), the largest single block size,
/// the chunk's total compressed/uncompressed sizes, then one size pair per
/// block.
/// </summary>
public sealed record ChunkHeader
{
    public required int BlockSize { get; init; }
    public required int TotalCompressedSize { get; init; }
    public required int TotalUncompressedSize { get; init; }
    public required IReadOnlyList<ChunkBlock> Blocks { get; init; }

    public int HeaderSize => (4 * 4) + (Blocks.Count * 4 * 2);

    public static ChunkHeader Read(ReadOnlySpan<byte> package, int offset)
    {
        var cursor = new PackageCursor(package, offset);

        uint magic = cursor.ReadUInt32("chunk magic");
        if (magic != PackageHeader.Magic)
            throw new InvalidPackageException($"Chunk at {offset} has magic 0x{magic:X8}, expected 0x{PackageHeader.Magic:X8}.");

        int blockSize = cursor.ReadInt32("chunk block size");
        int totalCompressed = cursor.ReadInt32("chunk compressed size");
        int totalUncompressed = cursor.ReadInt32("chunk uncompressed size");

        if (blockSize <= 0) throw new InvalidPackageException($"Chunk at {offset} declares block size {blockSize}.");
        if (totalUncompressed < 0 || totalCompressed < 0)
            throw new InvalidPackageException($"Chunk at {offset} declares negative sizes.");

        int blockCount = (totalUncompressed + blockSize - 1) / blockSize;
        if ((long)blockCount * 8 > cursor.Remaining)
            throw new InvalidPackageException($"Chunk at {offset} implies {blockCount} blocks, more than the file has room for.");

        var blocks = new ChunkBlock[blockCount];
        long sumCompressed = 0, sumUncompressed = 0;

        for (int i = 0; i < blockCount; i++)
        {
            int compressed = cursor.ReadInt32($"block {i} compressed size");
            int uncompressed = cursor.ReadInt32($"block {i} uncompressed size");

            if (compressed < 0 || uncompressed < 0) throw new InvalidPackageException($"Block {i} has a negative size.");
            if (uncompressed > blockSize) throw new InvalidPackageException($"Block {i} expands past the declared block size.");

            blocks[i] = new ChunkBlock(compressed, uncompressed);
            sumCompressed += compressed;
            sumUncompressed += uncompressed;
        }

        if (sumUncompressed != totalUncompressed || sumCompressed != totalCompressed)
            throw new InvalidPackageException($"Chunk at {offset}: block sizes don't sum to the chunk's declared totals.");

        return new ChunkHeader { BlockSize = blockSize, TotalCompressedSize = totalCompressed, TotalUncompressedSize = totalUncompressed, Blocks = blocks };
    }
}

/// <summary>Expands every compressed chunk of a package into one contiguous body.</summary>
public static class ChunkExpander
{
    /// <summary>
    /// The body starts at the first chunk's declared uncompressed offset, not
    /// at file offset zero — <paramref name="bodyStart"/> reports that origin
    /// so header-declared offsets can be converted into indices into the body.
    /// </summary>
    public static byte[] ExpandBody(PackageHeader header, ReadOnlySpan<byte> package, out int bodyStart)
    {
        if (!header.IsCompressed)
        {
            bodyStart = 0;
            return package.ToArray();
        }

        bodyStart = header.Chunks[0].UncompressedOffset;

        long totalLength = 0;
        foreach (PackageChunk chunk in header.Chunks) totalLength += chunk.UncompressedSize;
        if (totalLength > int.MaxValue) throw new InvalidPackageException($"Package body is {totalLength} bytes — too large.");

        byte[] body = new byte[totalLength];

        foreach (PackageChunk chunk in header.Chunks)
        {
            int writeAt = chunk.UncompressedOffset - bodyStart;
            if (writeAt < 0 || writeAt + chunk.UncompressedSize > body.Length)
                throw new InvalidPackageException($"Chunk at {chunk.UncompressedOffset} does not fit the expanded body.");

            ExpandChunk(package, chunk, body.AsSpan(writeAt, chunk.UncompressedSize));
        }

        return body;
    }

    public static void ExpandChunk(ReadOnlySpan<byte> package, PackageChunk chunk, Span<byte> destination)
    {
        ChunkHeader chunkHeader = ChunkHeader.Read(package, chunk.CompressedOffset);

        if (chunkHeader.TotalUncompressedSize != destination.Length)
            throw new InvalidPackageException(
                $"Chunk at {chunk.CompressedOffset} expands to {chunkHeader.TotalUncompressedSize} bytes but " +
                $"{destination.Length} were reserved for it.");

        int readAt = chunk.CompressedOffset + chunkHeader.HeaderSize;
        int writeAt = 0;

        foreach (ChunkBlock block in chunkHeader.Blocks)
        {
            if (readAt + block.CompressedSize > package.Length)
                throw new InvalidPackageException($"A block of the chunk at {chunk.CompressedOffset} runs past the end of the file.");

            Lzo1x.Decompress(
                package.Slice(readAt, block.CompressedSize),
                destination.Slice(writeAt, block.UncompressedSize));

            readAt += block.CompressedSize;
            writeAt += block.UncompressedSize;
        }
    }
}
