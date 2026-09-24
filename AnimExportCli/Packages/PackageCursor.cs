namespace AnimExportCli.Packages;

/// <summary>Thrown whenever package bytes don't match the layout a reader expects.</summary>
public sealed class InvalidPackageException(string message) : Exception(message);

/// <summary>
/// A forward-reading, bounds-checked cursor over a package's bytes.
/// </summary>
/// <remarks>
/// A UPK is untrusted input as far as this tool is concerned — nothing here
/// trusts a length or an offset the file itself declares without checking it
/// against the bytes actually available first.
/// </remarks>
public ref struct PackageCursor
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    public PackageCursor(ReadOnlySpan<byte> data, int position = 0)
    {
        _data = data;
        _position = position;
    }

    public readonly int Position => _position;
    public readonly int Length => _data.Length;
    public readonly int Remaining => _data.Length - _position;

    public void Seek(int position)
    {
        if (position < 0 || position > _data.Length)
            throw new InvalidPackageException($"Seek to {position} is outside the {_data.Length}-byte buffer.");
        _position = position;
    }

    public void Skip(int count) => Seek(_position + count);

    private readonly void Require(int count, string what)
    {
        if (count < 0)
            throw new InvalidPackageException($"Negative length {count} reading {what} at offset {_position}.");
        if (_position + count > _data.Length)
            throw new InvalidPackageException(
                $"Reading {what} at offset {_position} needs {count} bytes but only {Remaining} remain.");
    }

    public int ReadInt32(string what = "int32")
    {
        Require(4, what);
        int value = BitConverter.ToInt32(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public uint ReadUInt32(string what = "uint32")
    {
        Require(4, what);
        uint value = BitConverter.ToUInt32(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public short ReadInt16(string what = "int16")
    {
        Require(2, what);
        short value = BitConverter.ToInt16(_data.Slice(_position, 2));
        _position += 2;
        return value;
    }

    public ushort ReadUInt16(string what = "uint16")
    {
        Require(2, what);
        ushort value = BitConverter.ToUInt16(_data.Slice(_position, 2));
        _position += 2;
        return value;
    }

    public float ReadSingle(string what = "float")
    {
        Require(4, what);
        float value = BitConverter.ToSingle(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public byte ReadByte(string what = "byte")
    {
        Require(1, what);
        return _data[_position++];
    }

    public ulong ReadUInt64(string what = "uint64")
    {
        Require(8, what);
        ulong value = BitConverter.ToUInt64(_data.Slice(_position, 8));
        _position += 8;
        return value;
    }

    public Guid ReadGuid(string what = "guid")
    {
        Require(16, what);
        var value = new Guid(_data.Slice(_position, 16));
        _position += 16;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count, string what = "bytes")
    {
        Require(count, what);
        ReadOnlySpan<byte> value = _data.Slice(_position, count);
        _position += count;
        return value;
    }

    /// <summary>Peeks a byte without moving the cursor — for walking a fixed-stride array.</summary>
    public readonly byte PeekByte(int at)
    {
        if (at < 0 || at >= _data.Length) throw new InvalidPackageException($"Peek at {at} is outside the buffer.");
        return _data[at];
    }

    public readonly uint PeekUInt32(int at)
    {
        if (at < 0 || at + 4 > _data.Length) throw new InvalidPackageException($"Peek at {at} is outside the buffer.");
        return BitConverter.ToUInt32(_data.Slice(at, 4));
    }

    public readonly float PeekSingle(int at)
    {
        if (at < 0 || at + 4 > _data.Length) throw new InvalidPackageException($"Peek at {at} is outside the buffer.");
        return BitConverter.ToSingle(_data.Slice(at, 4));
    }

    public readonly Half PeekHalf(int at)
    {
        if (at < 0 || at + 2 > _data.Length) throw new InvalidPackageException($"Peek at {at} is outside the buffer.");
        return BitConverter.Int16BitsToHalf(BitConverter.ToInt16(_data.Slice(at, 2)));
    }

    /// <summary>
    /// A length-prefixed string. A positive length means single-byte characters;
    /// a negative length means UTF-16 and counts characters, not bytes. Both
    /// include a trailing null, which is stripped here.
    /// </summary>
    public string ReadString(string what = "string")
    {
        int length = ReadInt32($"{what} length");
        if (length == 0) return string.Empty;

        if (length > 0)
        {
            ReadOnlySpan<byte> raw = ReadBytes(length, what);
            if (raw.Length > 0 && raw[^1] == 0) raw = raw[..^1];
            return System.Text.Encoding.ASCII.GetString(raw);
        }

        ReadOnlySpan<byte> wide = ReadBytes(-length * 2, what);
        string decoded = System.Text.Encoding.Unicode.GetString(wide);
        return decoded.Length > 0 && decoded[^1] == '\0' ? decoded[..^1] : decoded;
    }
}
