namespace AnimExportCli.Packages.Properties;

/// <summary>
/// One entry from an object's tagged-property block: name, type, size and
/// array index, followed by that many bytes of value.
/// </summary>
public sealed record PropertyTag
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public required int ArrayIndex { get; init; }

    /// <summary>For a struct, its struct type name; for an enum-valued byte, the enum name; empty otherwise.</summary>
    public required string InnerName { get; init; }

    public required ReadOnlyMemory<byte> Value { get; init; }
}
