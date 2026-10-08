using MhoExtendedModManager;
using MemmSettings = MhoExtendedModManager.Settings;

namespace MhoExtendedModManager.Model;

// The MFF model importer's engine (Model\Mff, Retarget, Mho, Build, App), moved into the Mod Manager from the MHO MFF
// Importer (Kurt, 2026-10-03: the editor's Model tab). The engine's files keep their namespace and stay as in the importer;
// what it asked of the importer's own app (its settings, the stock package lookup, the folders it must not write into, the
// version) comes from the Mod Manager here.

/// <summary>The engine's settings, from the Mod Manager's: the game, the clean stock folder, the MFF folder, Blender.</summary>
sealed class Settings
{
    static Settings? current;
    public static Settings Current => current ??= Load();
    /// <summary>Settings changed in the Mod Manager (the MFF folder, the game): read again on next use.</summary>
    public static void Reset() => current = null;

    public string? MffSource { get; init; }
    /// <summary>The folder Browse for a Model opens in (Settings → Model → Change Models Folder); null = <see cref="LastModelFolder"/>.</summary>
    public string? ModelsFolder { get; init; }
    /// <summary>The folder the last model was picked from (model\settings.json).</summary>
    public string? LastModelFolder { get; set; }
    public string? GameFolder { get; init; }
    /// <summary>The clean stock packages folder (read only).</summary>
    public string? StockFolder { get; init; }
    public string? CookedFolder => GameFolder != null ? MemmSettings.Cooked(GameFolder) : null;
    public string? BlenderPath { get; set; }
    public bool SkipAddonOffer { get; set; }
    /// <summary>The Model tab's own remembered choices (model\settings.json): the preview's look and playback, FBX files picked.</summary>
    public PreviewPrefs Preview { get; set; } = new();
    public List<string> RecentFbx { get; set; } = new();
    /// <summary>The image editor the Materials tab opens maps in (null: the first one found).</summary>
    public string? ImageEditorPath { get; set; }
    /// <summary>The Model tab's panel sizes (column / row shares, the log's height) and folded panels.</summary>
    public Dictionary<string, float> Layout { get; set; } = new();
    public List<string> Folded { get; set; } = new();
    public bool RememberWindow { get; set; }

    public sealed class PreviewPrefs
    {
        public bool Loop { get; set; } = true;
        public bool Spec { get; set; } = true;
        public bool Reflect { get; set; } = true;
        public bool Glow { get; set; } = true;
        public bool Bloom { get; set; } = true;
        public bool Props { get; set; } = true;
        public bool Powers { get; set; } = true;
        public bool Bones { get; set; }
        public float Light { get; set; } = 1;
        public float Lens { get; set; } = 50;
        public float GlowStrength { get; set; } = 1;
    }

    sealed class Own { public PreviewPrefs Preview { get; set; } = new(); public List<string> RecentFbx { get; set; } = new(); public string? ImageEditorPath { get; set; } public Dictionary<string, float>? Layout { get; set; } public List<string>? Folded { get; set; } public string? LastModelFolder { get; set; } }
    static string OwnFile => Path.Combine(Home, "settings.json");

    /// <summary>The engine's own work folder (thumbnails, exports, command-line outputs): model\ in the Mod Manager's data.</summary>
    public static string Home => Path.Combine(MemmSettings.Home, "model");

    /// <summary>The main window's settings object, when one is open. It keeps its own copy and saves it (the window's place
    /// on exit), so the Model tab's choices are made on that copy: a change written only to the file was overwritten on exit
    /// (Kurt: the MFF folder was forgotten after a restart).</summary>
    public static MemmSettings? App { get; set; }

    /// <summary>Changes the Mod Manager's settings and saves them, on the main window's copy when there is one.</summary>
    public static void Change(Action<MemmSettings> change)
    {
        var s = App ?? MemmSettings.Load();
        change(s);
        s.Save();
    }

    static Settings Load()
    {
        var s = App ?? MemmSettings.Load();
        string? root = s.ResolvedGameRoot(MemmSettings.LibraryData(s.LibraryPath));
        return new Settings
        {
            MffSource = s.MffFolder is { Length: > 0 } m ? m : null, GameFolder = root,
            ModelsFolder = s.ModelsFolder is { Length: > 0 } mf ? mf : null,
            StockFolder = StockFiles.Clean ?? (s.CleanGameFiles is { Length: > 0 } c && Directory.Exists(c) ? c : null),
            BlenderPath = s.BlenderPath, SkipAddonOffer = s.SkipBlenderAddonOffer,
        }.WithOwn();
    }

    Settings WithOwn()
    {
        try
        {
            if (File.Exists(OwnFile) && System.Text.Json.JsonSerializer.Deserialize<Own>(File.ReadAllText(OwnFile)) is { } o) { Preview = o.Preview; RecentFbx = o.RecentFbx; ImageEditorPath = o.ImageEditorPath; Layout = o.Layout ?? new(); Folded = o.Folded ?? new(); LastModelFolder = o.LastModelFolder; }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        return this;
    }

    /// <summary>The Blender choices go back into the Mod Manager's settings.</summary>
    public void Save()
    {
        var s = App ?? MemmSettings.Load();
        if (s.BlenderPath != BlenderPath || s.SkipBlenderAddonOffer != SkipAddonOffer) Change(m => { m.BlenderPath = BlenderPath; m.SkipBlenderAddonOffer = SkipAddonOffer; });
        Directory.CreateDirectory(Home);
        File.WriteAllText(OwnFile, System.Text.Json.JsonSerializer.Serialize(new Own { Preview = Preview, RecentFbx = RecentFbx, ImageEditorPath = ImageEditorPath, Layout = Layout, Folded = Folded, LastModelFolder = LastModelFolder }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Base packages: the game's stock copy, as the Mod Manager reads stock files everywhere (StockFiles: the kept
/// originals, the clean folder, the live file, its .bak, whichever matches the stock checksums).</summary>
static class BasePackage
{
    static MhoExtendedModManager.GameState? game;

    public static string Resolve(string nameOrPath, bool allowModded = false)
    {
        string? path = File.Exists(nameOrPath) ? Path.GetFullPath(nameOrPath) : null;
        string? cooked = Settings.Current.CookedFolder;
        if (path == null && !nameOrPath.Contains('\\') && !nameOrPath.Contains('/') && cooked != null)
        {
            string file = nameOrPath.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? nameOrPath : nameOrPath + ".upk";
            string found = StockFiles.For(cooked, file);
            if (File.Exists(found)) path = found;
        }
        if (path == null) throw new FileNotFoundException($"no package {nameOrPath} (looked for the game's stock copy in {cooked ?? "(game not found)"}).");
        if (!allowModded && Settings.Current.GameFolder is string root)
        {
            game ??= new MhoExtendedModManager.GameState(root, MemmSettings.LibraryData(MemmSettings.Load().LibraryPath));
            string name = Path.GetFileName(path);
            if (!game.MatchesStock(name, path))
                throw new InvalidDataException($"{path} isn't the stock {name}: it has been modded. Set a clean stock folder (Settings), or turn its mod off and Apply.");
        }
        return path;
    }
}

/// <summary>Folders the engine never writes into: the game, the clean stock folder, the kept originals and the MFF source.
/// Every file it writes goes through <see cref="CheckWrite"/> (the Mod Manager itself writes the game only through Apply).</summary>
static class Protected
{
    static IEnumerable<string> Roots()
    {
        var st = Settings.Current;
        foreach (var r in new[] { st.MffSource, st.StockFolder, st.GameFolder, StockFiles.Clean })
            if (!string.IsNullOrEmpty(r)) yield return r;
    }

    public static void CheckWrite(string path)
    {
        string full = Path.GetFullPath(path);
        foreach (var root in Roots())
        {
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(full.TrimEnd('\\') + "\\", r, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refused: {full} is inside {root.TrimEnd('\\')}, which the model engine never writes to.");
        }
    }
}

/// <summary>What the engine asks of its app: the version (in mod notes and logs) and a package's name lookup for tagged
/// properties.</summary>
static class Program
{
    public static string Version => MhoExtendedModManager.Program.Version;
    internal static Func<long, string> Names(AnimExportCli.Packages.Package pkg) => r => pkg.Names.Resolve((int)(r & 0xFFFFFFFF), (int)(r >> 32));
}
