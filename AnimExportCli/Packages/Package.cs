using AnimExportCli.Packages.Compression;
using AnimExportCli.Packages.Properties;

namespace AnimExportCli.Packages;

/// <summary>A cooked UE3-family package, opened read-only: header, tables, and expanded body.</summary>
public sealed class Package
{
    private readonly byte[] _body;
    private readonly int _bodyStart;

    private Package(PackageHeader header, byte[] body, int bodyStart, NameTable names, ImportTable imports, ExportTable exports)
    {
        Header = header;
        _body = body;
        _bodyStart = bodyStart;
        Names = names;
        Imports = imports;
        Exports = exports;
    }

    public PackageHeader Header { get; }
    public NameTable Names { get; }
    public ImportTable Imports { get; }
    public ExportTable Exports { get; }

    public static Package Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllBytes(path));
    }

    public static Package Read(ReadOnlySpan<byte> file)
    {
        PackageHeader header = PackageHeader.Read(file);
        byte[] body = ChunkExpander.ExpandBody(header, file, out int bodyStart);

        return new Package(
            header, body, bodyStart,
            NameTable.Read(body, header, bodyStart),
            ImportTable.Read(body, header, bodyStart),
            ExportTable.Read(body, header, bodyStart));
    }

    public ReadOnlySpan<byte> GetExportData(int exportIndex)
    {
        ExportEntry export = Exports[exportIndex];
        int start = export.SerialOffset - _bodyStart;
        if (start < 0 || start + export.SerialSize > _body.Length)
            throw new InvalidPackageException($"Export {exportIndex} lies outside the expanded body.");

        return _body.AsSpan(start, export.SerialSize);
    }

    public string GetExportClassName(int exportIndex) => ResolveName(Exports[exportIndex].Class);

    public string GetExportName(int exportIndex) => Exports[exportIndex].ObjectName.Resolve(Names);

    public string ResolveName(ObjectReference reference)
    {
        if (reference.IsNull) return string.Empty;
        return reference.IsExport ? Exports[reference.ExportIndex].ObjectName.Resolve(Names) : Imports[reference.ImportIndex].ObjectName.Resolve(Names);
    }

    /// <summary>Every export whose class matches <paramref name="className"/> (case-insensitive).</summary>
    public IEnumerable<int> FindExportsOfClass(string className)
    {
        for (int i = 0; i < Exports.Count; i++)
        {
            if (string.Equals(GetExportClassName(i), className, StringComparison.OrdinalIgnoreCase))
                yield return i;
        }
    }

    /// <summary>
    /// Reads the tagged properties of an export. Returns null when the block
    /// doesn't parse — some objects are pure binary with no property block.
    /// </summary>
    /// <remarks>
    /// A component-type object writes sixteen bytes before its properties — a
    /// net index, then the name of the template it was made from, then the
    /// class that owns that template — rather than the plain four-byte net
    /// index an ordinary object leads with. Started at the ordinary offset,
    /// the reader lands mid-preamble and finds no properties at all. This is
    /// detected rather than assumed: a component writes its own name four
    /// bytes in, which is checked here before falling back to the wider offset.
    /// </remarks>
    public PropertyBag? TryReadProperties(int exportIndex)
    {
        ReadOnlySpan<byte> data = GetExportData(exportIndex);

        PropertyBag? bag = PropertyReader.TryRead(data, Names);
        if (bag is not null && bag.Tags.Count > 0) return bag;

        if (!HasComponentPreamble(data, exportIndex)) return bag;

        return PropertyReader.TryRead(data[ComponentPreambleBytes..], Names, skipLeadingNetIndex: false) ?? bag;
    }

    private const int ComponentPreambleBytes = 16;

    private bool HasComponentPreamble(ReadOnlySpan<byte> data, int exportIndex)
    {
        if (data.Length < ComponentPreambleBytes) return false;

        int index = BitConverter.ToInt32(data.Slice(4, 4));
        int number = BitConverter.ToInt32(data.Slice(8, 4));
        if ((uint)index >= (uint)Names.Count) return false;

        return Names.Resolve(index, number).Equals(GetExportName(exportIndex), StringComparison.OrdinalIgnoreCase);
    }
}
