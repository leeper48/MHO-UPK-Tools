namespace AnimExportCli.Packages.Properties;

/// <summary>
/// Reads the tagged-property block that leads most serialised UE3-family
/// objects: a sequence of (name, type, size, array-index) tags, each
/// followed by that many bytes of value, ending with a tag named "None".
/// </summary>
/// <remarks>
/// Three types carry an extra field between the array index and the value: a
/// struct names its struct type, an enum-valued byte names its enum, and a
/// bool carries its value inline in the tag with a declared size of zero.
/// Getting any of those wrong desyncs the stream and the "None" terminator
/// never turns up — which is what makes it a real correctness check rather
/// than a guess.
/// </remarks>
public static class PropertyReader
{
    private const string Terminator = "none";
    private const int MaxProperties = 65536;

    public static PropertyBag Read(ReadOnlySpan<byte> data, NameTable names, bool skipLeadingNetIndex = true)
    {
        var cursor = new PackageCursor(data);
        if (skipLeadingNetIndex)
        {
            if (data.Length < 4) throw new InvalidPackageException("Object data is too short to hold a net index.");
            cursor.Skip(4);
        }

        var tags = new List<PropertyTag>();

        while (true)
        {
            if (tags.Count > MaxProperties) throw new InvalidPackageException("Property block exceeded a sane number of entries.");

            string name = ReadName(ref cursor, names, "property name");
            if (string.Equals(name, Terminator, StringComparison.OrdinalIgnoreCase))
                return new PropertyBag(tags.ToArray(), cursor.Position);

            string typeName = ReadName(ref cursor, names, "property type");
            int size = cursor.ReadInt32("property size");
            int arrayIndex = cursor.ReadInt32("property array index");

            if (size < 0) throw new InvalidPackageException($"Property '{name}' declares a negative size.");
            if (arrayIndex < 0) throw new InvalidPackageException($"Property '{name}' declares a negative array index.");

            string innerName = string.Empty;
            byte[] value;

            switch (typeName.ToLowerInvariant())
            {
                case "structproperty":
                    innerName = ReadName(ref cursor, names, $"struct type of '{name}'");
                    value = cursor.ReadBytes(size, $"value of '{name}'").ToArray();
                    break;

                case "byteproperty":
                    innerName = ReadName(ref cursor, names, $"enum of '{name}'");
                    if (size == 8)
                    {
                        int valueIndex = cursor.ReadInt32($"enum value of '{name}'");
                        int valueNumber = cursor.ReadInt32($"enum value number of '{name}'");
                        innerName = names.Resolve(valueIndex, valueNumber);
                        value = BitConverter.GetBytes(valueIndex);
                    }
                    else
                    {
                        value = cursor.ReadBytes(size, $"value of '{name}'").ToArray();
                    }
                    break;

                case "boolproperty":
                    value = [cursor.ReadBytes(1, $"value of '{name}'")[0]];
                    break;

                case "nameproperty":
                {
                    int valueIndex = cursor.ReadInt32($"name value of '{name}'");
                    int valueNumber = cursor.ReadInt32($"name value number of '{name}'");
                    innerName = names.Resolve(valueIndex, valueNumber);
                    value = BitConverter.GetBytes(valueIndex);
                    break;
                }

                default:
                    value = cursor.ReadBytes(size, $"value of '{name}'").ToArray();
                    break;
            }

            tags.Add(new PropertyTag { Name = name, TypeName = typeName, ArrayIndex = arrayIndex, InnerName = innerName, Value = value });
        }
    }

    public static PropertyBag? TryRead(ReadOnlySpan<byte> data, NameTable names, bool skipLeadingNetIndex = true)
    {
        try
        {
            return Read(data, names, skipLeadingNetIndex);
        }
        catch (InvalidPackageException)
        {
            return null;
        }
    }

    private static string ReadName(ref PackageCursor cursor, NameTable names, string what)
    {
        int index = cursor.ReadInt32($"{what} index");
        int number = cursor.ReadInt32($"{what} number");

        if ((uint)index >= (uint)names.Count)
            throw new InvalidPackageException($"{what} refers to name {index}, outside the {names.Count}-entry table.");

        return names.Resolve(index, number);
    }
}
