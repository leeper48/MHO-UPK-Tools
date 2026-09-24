namespace AnimExportCli.Packages;

/// <summary>
/// A reference to an object, encoded as a signed index: zero is null, a
/// positive value is an export at <c>value - 1</c>, a negative value is an
/// import at <c>-value - 1</c>.
/// </summary>
public readonly record struct ObjectReference(int Value)
{
    public static readonly ObjectReference Null = new(0);

    public bool IsNull => Value == 0;
    public bool IsExport => Value > 0;
    public bool IsImport => Value < 0;

    public int ExportIndex => IsExport ? Value - 1 : throw new InvalidOperationException($"{Value} is not an export.");
    public int ImportIndex => IsImport ? -Value - 1 : throw new InvalidOperationException($"{Value} is not an import.");

    public override string ToString() => Value switch
    {
        0 => "null",
        > 0 => $"export[{Value - 1}]",
        _ => $"import[{-Value - 1}]",
    };
}

/// <summary>A name-table index plus a disambiguating number.</summary>
public readonly record struct NameReference(int Index, int Number)
{
    public string Resolve(NameTable names) => names.Resolve(Index, Number);
}
