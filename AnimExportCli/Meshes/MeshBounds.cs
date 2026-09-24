namespace AnimExportCli.Meshes;

/// <summary>The volume a mesh occupies: a centre, a half-size on each axis, and an enclosing sphere radius.</summary>
public readonly record struct MeshBounds(float OriginX, float OriginY, float OriginZ, float ExtentX, float ExtentY, float ExtentZ, float Radius)
{
    public const int ByteSize = 4 * 7;

    public static MeshBounds Read(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + ByteSize > data.Length)
            throw new Packages.InvalidPackageException($"Mesh bounds at offset {offset} lie outside the {data.Length}-byte object.");

        return new MeshBounds(
            BitConverter.ToSingle(data[offset..]),
            BitConverter.ToSingle(data[(offset + 4)..]),
            BitConverter.ToSingle(data[(offset + 8)..]),
            BitConverter.ToSingle(data[(offset + 12)..]),
            BitConverter.ToSingle(data[(offset + 16)..]),
            BitConverter.ToSingle(data[(offset + 20)..]),
            BitConverter.ToSingle(data[(offset + 24)..]));
    }
}
