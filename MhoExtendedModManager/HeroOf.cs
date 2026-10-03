namespace MhoExtendedModManager;

/// <summary>
/// The hero a character package belongs to, for powers and effects (Kurt, 2026-10-03: Kate Bishop showed no powers). A
/// package is named after its class (UC__&lt;class&gt;_SF); most costume classes start with their hero's
/// (MarvelPlayer_Storm_Modern), but some don't: Kate Bishop (Young Avengers) is a Hawkeye costume of class
/// MarvelPlayer_KateBishop. The game data's costume (CostumeUnrealClass → UsableBy, the avatar) says whose it is; without it,
/// the name's hero part as before.
/// </summary>
static class HeroOf
{
    static readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="cooked">The game's CookedPCConsole (its game data is read for the costumes); null: the name only.</param>
    public static string? Package(string packageFile, string? cooked)
    {
        var parts = Path.GetFileNameWithoutExtension(packageFile).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) || !parts[1].StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase)) return null;
        string byName = parts[2];
        if (cooked == null) return byName;
        string cls = string.Join("_", parts.Skip(1).Take(parts.Length - 1 - (parts[^1].Equals("SF", StringComparison.OrdinalIgnoreCase) ? 1 : 0)));
        lock (cache) if (cache.TryGetValue(cls, out var hit)) return hit ?? byName;
        string? hero = null;
        try
        {
            string root = Path.GetFullPath(Path.Combine(cooked, "..", "..", ".."));
            if (Costume.All(root)?.FirstOrDefault(c => c.Class.Equals(cls, StringComparison.OrdinalIgnoreCase) && c.Hero != null) is { } c)
                hero = Path.GetFileNameWithoutExtension(c.Hero!.Replace('\\', '/'));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        lock (cache) cache[cls] = hero;
        return hero ?? byName;
    }
}
