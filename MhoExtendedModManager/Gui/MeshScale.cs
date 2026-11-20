namespace MhoExtendedModManager.Gui;

/// <summary>
/// The size the game draws a costume at (Kurt, 2026-11-20: the Mods tab's 3D view showed ULTRAVISION at 100 % beside the
/// barrel, though its package sets 123 %): its class default's mesh component Scale, else (a costume that doesn't set one)
/// its hero's base component's, as the game takes the costume's component's template; else 1. The mod's copy of a package is
/// read before the game's. The game scales from the feet (stock Carnage at 110 % stands on the floor), as the 3D view does.
/// </summary>
static class MeshScale
{
    public static float Of(string packageFile, IReadOnlyList<(string File, string Path)> modPackages, string? cooked)
    {
        string name = Path.GetFileName(packageFile);
        if (Own(packageFile, name) is float own) return own;
        string stem = Stem(name);
        var parts = stem.Split('_');
        if (parts.Length < 3 || !parts[0].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase)) return 1;
        string baseName = $"UC__MarvelPlayer_{parts[1]}_SF.upk";
        string? basePath = modPackages.FirstOrDefault(p => p.File.Equals(baseName, StringComparison.OrdinalIgnoreCase)).Path
            ?? (cooked != null ? Path.Combine(cooked, baseName) : null);
        return basePath != null && File.Exists(basePath) && Own(basePath, baseName) is float b ? b : 1;
    }

    static string Stem(string file)
    {
        string s = Path.GetFileNameWithoutExtension(file);
        if (s.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        if (s.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) s = s[..^3];
        return s;
    }

    /// <summary>The Scale the package's own class default component sets, or null (none set, no component, unreadable).</summary>
    static float? Own(string path, string name)
    {
        try
        {
            var p = MhoPackageModifier.Package.Open(path);
            string comp = $"marvelgamecontent.default__{Stem(name).ToLowerInvariant()}.initialskeletalmesh";
            int i = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(comp, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            byte[] d = p.ReadExportBytes(p.Exports[i]).ToArray();
            var t = MhoPackageModifier.TagWalker.Walk(p, d, 16)?.FirstOrDefault(x => x.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase));
            if (t == null) return null;
            float v = BitConverter.ToSingle(d, t.ValueAt);
            return v > 0.01f && v < 100 ? v : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or MhoPackageModifier.PackageFormatException) { return null; }
    }
}
