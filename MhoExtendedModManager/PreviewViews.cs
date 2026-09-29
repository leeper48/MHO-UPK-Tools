using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// The 3D preview's saved camera per mod and mesh (Kurt: rotate / zoom / pan remembered), the animation and the frame it
/// was left on (Kurt: kept when leaving the mod and coming back), and whether animations loop.
/// Kept in data\preview_views.json, not state.json: turning a model isn't a library change, so it stays out of Undo.
/// Views are relative to the mesh (MeshViewer.ViewState), so they still fit when the mesh changes.
/// </summary>
static class PreviewViews
{
    sealed class Data
    {
        public Dictionary<string, float[]> Views { get; set; } = [];
        public Dictionary<string, AnimState> Anims { get; set; } = [];
        public bool Loop { get; set; } = true;
        /// <summary>The 3D preview's shading toggles (Kurt): specular highlights, reflections, glow. Remembered on this PC.</summary>
        public bool Spec { get; set; } = true;
        public bool Reflect { get; set; } = true;
        public bool Glow { get; set; } = true;
        public Dictionary<string, float> Lights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The 3D preview's lens (35 mm-equivalent focal length) per mod on this PC (Kurt, from a user).</summary>
        public Dictionary<string, float> Lenses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IconSetup> Icons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    static Data? data;
    static string FilePath => Path.Combine(Settings.Home, "preview_views.json");
    static Data D => data ??= Load();

    static Data Load()
    {
        try
        {
            var d = File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new();
            d.Lights = new(d.Lights ?? [], StringComparer.OrdinalIgnoreCase);
            d.Lenses = new(d.Lenses ?? [], StringComparer.OrdinalIgnoreCase);
            d.Icons = new(d.Icons ?? [], StringComparer.OrdinalIgnoreCase);
            return d;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    static void Save()
    {
        try { Directory.CreateDirectory(Settings.Home); File.WriteAllText(FilePath, JsonSerializer.Serialize(D, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Key(Mod m, MeshRef mesh) => Key(m, mesh.Key);
    public static string Key(Mod m, string meshKey) => m.FolderName + "|" + meshKey;

    /// <summary>This PC's saved views for a mod, by mesh key (for putting them into an exported copy).</summary>
    public static Dictionary<string, float[]> ForMod(Mod m) =>
        D.Views.Where(kv => kv.Key.StartsWith(m.FolderName + "|", StringComparison.OrdinalIgnoreCase))
               .ToDictionary(kv => kv.Key[(m.FolderName.Length + 1)..], kv => kv.Value);
    public static float[]? Get(string key) => D.Views.TryGetValue(key, out var v) ? v : null;

    /// <summary>Saves a view (null forgets it).</summary>
    public static void Set(string key, float[]? view)
    {
        if (view == null) { if (!D.Views.Remove(key)) return; }
        else D.Views[key] = view;
        Save();
    }

    /// <summary>An animation shown in the 3D view and the time (seconds) it was left at.</summary>
    public sealed class AnimState
    {
        public string Name { get; set; } = "";
        public double Time { get; set; }
    }

    public static AnimState? GetAnim(string key) => D.Anims.TryGetValue(key, out var a) ? a : null;

    /// <summary>Saves the animation and time for a mesh (null: the rest pose, forgotten). Written only when it changed.</summary>
    public static void SetAnim(string key, string? name, double time)
    {
        if (name == null) { if (!D.Anims.Remove(key)) return; }
        else if (D.Anims.TryGetValue(key, out var a) && a.Name == name && Math.Abs(a.Time - time) < 1e-4) return;
        else D.Anims[key] = new AnimState { Name = name, Time = Math.Round(time, 4) };
        Save();
    }

    /// <summary>
    /// The 3D view's light brightness for a mod (0.5–2): per mod, since some mods' textures are darker than others (Kurt).
    /// This PC's value, else the mod author's (manifest PreviewLight, Kurt: modders can export it), else 1.
    /// </summary>
    public static float Light(Mod m) => LocalLight(m) ?? AuthorLight(m);

    /// <summary>The value set on this PC, or null.</summary>
    public static float? LocalLight(Mod m) => D.Lights.TryGetValue(m.FolderName, out float v) ? Math.Clamp(v, 0.5f, 2f) : null;

    /// <summary>The mod author's value, else 1 (what the slider's double-click goes back to).</summary>
    public static float AuthorLight(Mod m) => m.Manifest.PreviewLight is float a ? Math.Clamp(a, 0.5f, 2f) : 1f;

    /// <summary>The 3D preview's lens for a mod (the view's default when none is kept).</summary>
    public static float Lens(Mod m) => D.Lenses.TryGetValue(m.FolderName, out float v) ? Math.Clamp(v, 15f, 200f) : Gui.ModelView.DefaultFocalLength;

    public static void SetLens(Mod m, float value)
    {
        value = Math.Clamp(value, 15f, 200f);
        if (Math.Abs(value - Gui.ModelView.DefaultFocalLength) < 1e-3) { if (!D.Lenses.Remove(m.FolderName)) return; }
        else if (D.Lenses.TryGetValue(m.FolderName, out float old) && Math.Abs(old - value) < 1e-3) return;
        else D.Lenses[m.FolderName] = value;
        Save();
    }

    /// <summary>Sets this PC's value; the same as the mod's own forgets it (so a later author change comes through).</summary>
    public static void SetLight(Mod m, float value)
    {
        value = Math.Clamp(value, 0.5f, 2f);
        bool isAuthor = Math.Abs(value - AuthorLight(m)) < 1e-4;
        if (isAuthor ? !D.Lights.ContainsKey(m.FolderName) : D.Lights.TryGetValue(m.FolderName, out float old) && Math.Abs(old - value) < 1e-4) return;
        if (isAuthor) D.Lights.Remove(m.FolderName); else D.Lights[m.FolderName] = value;
        Save();
    }

    /// <summary>
    /// How an icon was set up in the editor's Create from 3D (Kurt: each icon its own pose and camera): the character
    /// (mesh key, and its file when it came from Open a .UPK), animation and frame, camera (ModelView.ViewState), light,
    /// background (0 transparent, 1 portrait backdrop, 2 color, 3 picture) and the original's overlay opacity.
    /// </summary>
    public sealed class IconSetup
    {
        public string? Mesh { get; set; }
        public string? MeshFile { get; set; }
        public string? Anim { get; set; }
        public float Frame { get; set; }
        public float[]? View { get; set; }
        public float Light { get; set; } = 1;
        /// <summary>The lens (35 mm equivalent focal length); 0 = the view's default.</summary>
        public float Lens { get; set; }
        public int Back { get; set; } = -1;
        public int BackColor { get; set; }
        public string? BackPicture { get; set; }
        public float Overlay { get; set; } = 0.35f;
        /// <summary>Props shown with the character (mesh keys); null = the ones the game attaches.</summary>
        public List<string>? Props { get; set; }
    }

    /// <summary>An icon's setup, by "&lt;mod&gt;|&lt;texture&gt;" (null when never set up).</summary>
    public static IconSetup? GetIcon(string key) => D.Icons.TryGetValue(key, out var s) ? s : null;
    public static void SetIcon(string key, IconSetup s) { D.Icons[key] = s; Save(); }
    public static void ForgetIcon(string key) { if (D.Icons.Remove(key)) Save(); }
    public static float Overlay { get => D.Icons.TryGetValue("*", out var s) ? s.Overlay : 0.35f; set { D.Icons["*"] = new IconSetup { Overlay = value }; Save(); } }

    public static bool Loop
    {
        get => D.Loop;
        set { if (D.Loop == value) return; D.Loop = value; Save(); }
    }

    public static bool Spec { get => D.Spec; set { if (D.Spec == value) return; D.Spec = value; Save(); } }
    public static bool Reflect { get => D.Reflect; set { if (D.Reflect == value) return; D.Reflect = value; Save(); } }
    public static bool Glow { get => D.Glow; set { if (D.Glow == value) return; D.Glow = value; Save(); } }
}
