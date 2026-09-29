namespace MhoExtendedModManager;

/// <summary>
/// Custom pictures for a mod's preview and card (a user's request, 2026-09-29: "a custom preview image and/or thumbnail").
/// Keys are "file:&lt;path&gt;": relative to the mod folder for the mod's own (in its Pictures\ folder, set in the editor,
/// they travel with the mod), or absolute for a user's own pick on this PC (kept in data\pictures\&lt;mod folder&gt;\).
/// Any .png / .jpg / .bmp / .dds works. An exported mod carries a user's pick as its own when they say so.
/// </summary>
static class ModPictures
{
    public const string Folder = "Pictures";
    public const string Prefix = "file:";
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".dds"];
    public const string DialogFilter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.dds";

    public static bool IsFile(string? key) => key != null && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The picture a "file:" key names, if it's there (relative keys are in <paramref name="modFolder"/>).</summary>
    public static string? Resolve(string modFolder, string? key)
    {
        if (!IsFile(key)) return null;
        string rel = key![Prefix.Length..];
        string path = Path.IsPathRooted(rel) ? rel : Path.Combine(modFolder, rel.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>The mod's own custom pictures: (key, file) for each image in its Pictures\ folder.</summary>
    public static List<(string Key, string File)> Own(string modFolder)
    {
        string dir = Path.Combine(modFolder, Folder);
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir).Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => (Prefix + Folder + "/" + Path.GetFileName(f), f))];
    }

    /// <summary>A user's own pick, copied to data\pictures\&lt;mod folder&gt;\&lt;kind&gt;.&lt;ext&gt; (replacing an earlier one); its key.</summary>
    public static string KeepLocal(string dataFolder, string modFolderName, string source, string kind)
    {
        string dir = Path.Combine(dataFolder, "pictures", modFolderName);
        Directory.CreateDirectory(dir);
        foreach (string old in Directory.EnumerateFiles(dir, kind + ".*")) File.Delete(old);
        string dest = Path.Combine(dir, kind + Path.GetExtension(source).ToLowerInvariant());
        File.Copy(source, dest, overwrite: true);
        return Prefix + dest;
    }

    /// <summary>A key's short name for menus and captions ("Pictures/card.png" → "card.png").</summary>
    public static string Label(string key) => Path.GetFileName(key[Prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
}
