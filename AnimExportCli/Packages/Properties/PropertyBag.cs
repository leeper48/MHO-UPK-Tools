namespace AnimExportCli.Packages.Properties;

/// <summary>
/// The tagged properties of one serialised object, plus where its binary
/// payload (if any) begins. Property names are matched case-insensitively —
/// the name table stores them lower-cased.
/// </summary>
public sealed class PropertyBag
{
    private readonly PropertyTag[] _tags;
    private readonly Dictionary<string, PropertyTag> _byName;

    internal PropertyBag(PropertyTag[] tags, int payloadOffset)
    {
        _tags = tags;
        PayloadOffset = payloadOffset;

        _byName = new Dictionary<string, PropertyTag>(tags.Length, StringComparer.OrdinalIgnoreCase);
        foreach (PropertyTag tag in tags) _byName.TryAdd(tag.Name, tag); // array elements repeat a name; keep the first
    }

    public IReadOnlyList<PropertyTag> Tags => _tags;

    /// <summary>Where the properties end and the object's own binary payload begins.</summary>
    public int PayloadOffset { get; }

    public PropertyTag? Find(string name) => _byName.GetValueOrDefault(name);

    public IEnumerable<PropertyTag> FindAll(string name) =>
        _tags.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public int GetInt(string name, int fallback = 0)
    {
        PropertyTag? tag = Find(name);
        return tag is not null && tag.Value.Length >= 4 ? BitConverter.ToInt32(tag.Value.Span) : fallback;
    }

    public float GetFloat(string name, float fallback = 0f)
    {
        PropertyTag? tag = Find(name);
        return tag is not null && tag.Value.Length >= 4 ? BitConverter.ToSingle(tag.Value.Span) : fallback;
    }

    /// <summary>Bool properties carry their value in the tag itself as one byte, not a value block.</summary>
    public bool GetBool(string name, bool fallback = false)
    {
        PropertyTag? tag = Find(name);
        return tag is not null && tag.Value.Length >= 1 ? tag.Value.Span[0] != 0 : fallback;
    }

    /// <summary>A name- or enum-valued property, already resolved to text by the reader.</summary>
    public string GetName(string name, string fallback = "")
    {
        PropertyTag? tag = Find(name);
        return tag is not null && !string.IsNullOrEmpty(tag.InnerName) ? tag.InnerName : fallback;
    }
}
