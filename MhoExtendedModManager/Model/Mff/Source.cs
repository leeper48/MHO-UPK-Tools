namespace MhoExtendedModManager.Model;

/// <summary>Kurt's MFF rip repository (read only): Models\Models\&lt;model&gt;\&lt;model&gt;.fbx and a flat Texture2D folder.
/// Other rips lay it out a little differently (Kurt, 2026-10-06): one Models level (Models\&lt;model&gt;), the model folders right
/// in the picked folder, Texture2D twice or beside a picked Models folder; each is found (<see cref="Layout"/>).
/// MHO_MFF_SOURCE overrides the root (tests).</summary>
static class Source
{
    public static string Root => Environment.GetEnvironmentVariable("MHO_MFF_SOURCE") is { Length: > 0 } s ? s
        : Settings.Current.MffSource ?? throw new InvalidOperationException(@"the MFF folder isn't set: --settings mff=<folder with Models\Models and Texture2D>");
    public static string Models => Layout(Root).Models;
    public static string Textures => Layout(Root).Textures;

    static readonly Dictionary<string, (string Models, string Textures)> layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where a rip folder keeps its model folders and its textures. Models: Models\Models, Models, or the folder itself (the
    /// first that holds model folders: a subfolder with an .fbx / .dae / .obj); textures: Texture2D, Texture2D\Texture2D,
    /// Textures, Models\Texture2D, or a Texture2D beside the picked folder (when the Models folder itself was picked), the
    /// first with pictures. Unknown layouts give the usual paths (Models\Models, Texture2D). Kept per folder for the session.
    /// </summary>
    public static (string Models, string Textures) Layout(string root)
    {
        lock (layouts) if (layouts.TryGetValue(root, out var hit)) return hit;
        string parent = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\', '/')) ?? root;
        static bool HasModels(string d)
        {
            if (!Directory.Exists(d)) return false;
            try
            {
                return Directory.EnumerateDirectories(d).Take(40).Any(sub => Directory.EnumerateFiles(sub).Any(f =>
                    f.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dae", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        static bool HasPictures(string d)
        {
            if (!Directory.Exists(d)) return false;
            try { return Directory.EnumerateFiles(d, "*.png").Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        string models = new[] { Path.Combine(root, "Models", "Models"), Path.Combine(root, "Models"), root }.FirstOrDefault(HasModels)
            ?? Path.Combine(root, "Models", "Models");
        string textures = new[] { Path.Combine(root, "Texture2D"), Path.Combine(root, "Texture2D", "Texture2D"), Path.Combine(root, "Textures"),
                                  Path.Combine(root, "Models", "Texture2D"), Path.Combine(parent, "Texture2D"), Path.Combine(parent, "Texture2D", "Texture2D") }.FirstOrDefault(HasPictures)
            ?? Path.Combine(root, "Texture2D");
        lock (layouts) layouts[root] = (models, textures);
        return (models, textures);
    }

    /// <summary>The folder was changed (Settings → Model): look again.</summary>
    public static void ForgetLayouts() { lock (layouts) layouts.Clear(); }

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
