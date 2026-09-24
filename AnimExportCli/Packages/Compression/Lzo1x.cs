namespace AnimExportCli.Packages.Compression;

/// <summary>
/// LZO1X block decompression.
/// </summary>
/// <remarks>
/// LZO1X is a published, license-neutral compression format (the format
/// itself, as distinct from Markus Oberhumer's GPL-licensed reference
/// implementation, is just a byte-stream specification). This is a clean-room
/// decoder written against that specification, not a port of any existing
/// LZO source.
/// <para>
/// The decoder is a single flat state machine — literal runs and matches share
/// exit points in the original format description, and splitting that into
/// nested loops would just make the two paths harder to line up against the
/// spec while reviewing.
/// </para>
/// </remarks>
public static class Lzo1x
{
    private const int ShortMatchMaxOffset = 0x0800;

    public static byte[] Decompress(ReadOnlySpan<byte> source, int uncompressedSize)
    {
        if (uncompressedSize < 0) throw new InvalidPackageException($"Negative uncompressed size {uncompressedSize}.");

        byte[] output = new byte[uncompressedSize];
        int written = Decompress(source, output);

        if (written != uncompressedSize)
        {
            throw new InvalidPackageException(
                $"Decompressed {written} bytes but the block header declared {uncompressedSize}.");
        }

        return output;
    }

    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty) throw new InvalidPackageException("Cannot decompress an empty block.");

        int readAt = 0, writeAt = 0, backref, length;

        // A first byte above 17 opens the stream with a literal run whose
        // length needs no extra decoding.
        if (source[0] > 17)
        {
            length = NextByte(source, ref readAt) - 17;
            if (length >= 4)
            {
                CopyLiterals(source, ref readAt, destination, ref writeAt, length);
                goto AfterFirstLiterals;
            }
            goto ShortLiteralRun;
        }

    NextInstruction:
        length = NextByte(source, ref readAt);
        if (length >= 16) goto MatchInstruction;

        if (length == 0) length = ReadRunOn(source, ref readAt, bias: 15, cap: destination.Length);
        CopyLiterals(source, ref readAt, destination, ref writeAt, length + 3);

    AfterFirstLiterals:
        length = NextByte(source, ref readAt);
        if (length >= 16) goto MatchInstruction;

        backref = writeAt - (1 + ShortMatchMaxOffset) - (length >> 2) - (NextByte(source, ref readAt) << 2);
        CopyMatch(destination, ref writeAt, backref, 3);
        goto AfterMatch;

    MatchInstruction:
        if (length >= 64)
        {
            backref = writeAt - 1 - ((length >> 2) & 7) - (NextByte(source, ref readAt) << 3);
            length = (length >> 5) - 1;
        }
        else if (length >= 32)
        {
            length &= 31;
            if (length == 0) length = ReadRunOn(source, ref readAt, bias: 31, cap: destination.Length);
            backref = writeAt - 1 - ReadDistance(source, ref readAt);
        }
        else if (length >= 16)
        {
            backref = writeAt - ((length & 8) << 11);
            length &= 7;
            if (length == 0) length = ReadRunOn(source, ref readAt, bias: 7, cap: destination.Length);
            backref -= ReadDistance(source, ref readAt);

            // A back-reference that resolves to the current write position is
            // the end-of-stream marker rather than a real match.
            if (backref == writeAt) return writeAt;

            backref -= 0x4000;
        }
        else
        {
            backref = writeAt - 1 - (length >> 2) - (NextByte(source, ref readAt) << 2);
            CopyMatch(destination, ref writeAt, backref, 2);
            goto AfterMatch;
        }

        CopyMatch(destination, ref writeAt, backref, length + 2);

    AfterMatch:
        // The low two bits of the byte two back from the read cursor carry a
        // short literal run that trails the match.
        if (readAt < 2) throw new InvalidPackageException("Malformed stream: reached match state before any operand bytes.");
        length = source[readAt - 2] & 3;
        if (length == 0) goto NextInstruction;

    ShortLiteralRun:
        CopyLiterals(source, ref readAt, destination, ref writeAt, length);
        length = NextByte(source, ref readAt);
        goto MatchInstruction;
    }

    private static byte NextByte(ReadOnlySpan<byte> source, ref int readAt)
    {
        if (readAt >= source.Length) throw new InvalidPackageException($"Compressed stream ended early at offset {readAt}.");
        return source[readAt++];
    }

    private static int ReadDistance(ReadOnlySpan<byte> source, ref int readAt)
    {
        byte low = NextByte(source, ref readAt);
        byte high = NextByte(source, ref readAt);
        return (low >> 2) + (high << 6);
    }

    private static int ReadRunOn(ReadOnlySpan<byte> source, ref int readAt, int bias, int cap)
    {
        int length = 0;
        while (true)
        {
            byte b = NextByte(source, ref readAt);
            if (b != 0) return length + bias + b;

            length += 255;
            if (length > cap) throw new InvalidPackageException($"Run-on length exceeded the {cap}-byte output; the block is corrupt.");
        }
    }

    private static void CopyLiterals(ReadOnlySpan<byte> source, ref int readAt, Span<byte> destination, ref int writeAt, int count)
    {
        if (count < 0) throw new InvalidPackageException($"Negative literal run {count}.");
        if (readAt + count > source.Length) throw new InvalidPackageException($"Literal run of {count} at input offset {readAt} runs past the block.");
        if (writeAt + count > destination.Length) throw new InvalidPackageException($"Literal run of {count} would overflow the {destination.Length}-byte output.");

        source.Slice(readAt, count).CopyTo(destination.Slice(writeAt, count));
        readAt += count;
        writeAt += count;
    }

    /// <summary>
    /// Copies from earlier in the output. Ranges can overlap — that's how runs
    /// get encoded — so this must stay byte-by-byte rather than a bulk copy.
    /// </summary>
    private static void CopyMatch(Span<byte> destination, ref int writeAt, int from, int count)
    {
        if (from < 0) throw new InvalidPackageException($"Back-reference to {from} points before the start of the output.");
        if (count < 0) throw new InvalidPackageException($"Negative match length {count}.");
        if (from + count > destination.Length || writeAt + count > destination.Length)
            throw new InvalidPackageException($"Match of {count} bytes would overflow the {destination.Length}-byte output.");

        for (int i = 0; i < count; i++)
            destination[writeAt++] = destination[from++];
    }
}
