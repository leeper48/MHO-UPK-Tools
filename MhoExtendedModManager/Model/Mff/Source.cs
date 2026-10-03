namespace MhoMffImporter;

/// <summary>Kurt's MFF rip repository (read only): Models\Models\&lt;model&gt;\&lt;model&gt;.fbx and a flat Texture2D folder.
/// MHO_MFF_SOURCE overrides the root (tests).</summary>
static class Source
{
    public static string Root => Environment.GetEnvironmentVariable("MHO_MFF_SOURCE") is { Length: > 0 } s ? s
        : Settings.Current.MffSource ?? throw new InvalidOperationException(@"the MFF folder isn't set: --settings mff=<folder with Models\Models and Texture2D>");
    public static string Models => Path.Combine(Root, "Models", "Models");
    public static string Textures => Path.Combine(Root, "Texture2D");

    /// <summary>A model folder by name (any case), a folder path, or a model file path.</summary>
    public static string ResolveModelFile(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        string dir = Directory.Exists(nameOrPath) ? nameOrPath : Path.Combine(Models, nameOrPath);
        if (!Directory.Exists(dir))
        {
            var match = Directory.Exists(Models)
                ? Directory.EnumerateDirectories(Models).FirstOrDefault(d => string.Equals(Path.GetFileName(d), nameOrPath, StringComparison.OrdinalIgnoreCase))
                : null;
            dir = match ?? throw new FileNotFoundException($"No model folder or file named {nameOrPath} (looked in {Models}).");
        }
        foreach (var ext in new[] { ".fbx", ".dae", ".obj" })
        {
            var f = Directory.EnumerateFiles(dir).FirstOrDefault(p => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (f != null) return f;
        }
        throw new FileNotFoundException($"No .fbx / .dae / .obj in {dir}.");
    }

    public static IEnumerable<string> AllModelFolders() =>
        Directory.EnumerateDirectories(Models).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
}
