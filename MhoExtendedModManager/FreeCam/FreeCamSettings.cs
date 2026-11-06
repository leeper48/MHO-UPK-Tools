using System.Text.Json;

namespace MhoExtendedModManager.FreeCam;

/// <summary>
/// FreeCam Options's choices (data\freecam\settings.json). Bindings are written "[Ctrl+][Shift+][Alt+]Key" for the keyboard and
/// "[RT+]XboxTypeS_Button" for a gamepad; empty = none.
/// </summary>
sealed class FreeCamSettings
{
    public string ModeKey { get; set; } = "F8";
    /// <summary>Snap Behind: swings the camera behind the hero once (Free Orbit stays free).</summary>
    public string SnapKey { get; set; } = "F4";
    /// <summary>Each action's gamepad button ("[RT+]XboxTypeS_…", empty = none). Both may use the same button (and RT
    /// setting) when their press types differ.</summary>
    public string ModePad { get; set; } = "";
    public string SnapPad { get; set; } = "";
    /// <summary>How each action's button is pressed: "" (none), "Short" (under a second), "Long" (held a second),
    /// "Double" (two presses).</summary>
    public string ModePress { get; set; } = "Short";
    public string SnapPress { get; set; } = "Long";
    /// <summary>0.60.410–0.60.412's one shared button: read from an older settings.json into both actions.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PadButton { get; set; }

    /// <summary>Selfie: like Snap Behind, from the front (the camera faces the hero).</summary>
    public string SelfieKey { get; set; } = "F3";
    public string SelfiePad { get; set; } = "";
    public string SelfiePress { get; set; } = "";

    /// <summary>The actions in order: name for the game code, keyboard spec, gamepad button, press.</summary>
    public (string Action, string Key, string Pad, string Press)[] Actions() =>
        [("Mode", ModeKey, ModePad, ModePress), ("Snap", SnapKey, SnapPad, SnapPress), ("Selfie", SelfieKey, SelfiePad, SelfiePress)];

    /// <summary>The gamepad buttons in use (button + RT setting), each with the actions on it and their press types, in
    /// the order the actions are listed (one press tracker each, up to three). Within a button each press type does one action
    /// (the first listed wins; the tab doesn't let two share one).</summary>
    public List<(string Pad, List<(string Action, string Press)> Actions)> PadGroups()
    {
        var groups = new List<(string Pad, List<(string Action, string Press)> Actions)>();
        foreach (var (action, _, pad, press) in Actions())
        {
            if (pad.Length == 0 || press.Length == 0) continue;
            int i = groups.FindIndex(g => g.Pad.Equals(pad, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { groups.Add((pad, [])); i = groups.Count - 1; }
            if (!groups[i].Actions.Any(x => x.Press == press)) groups[i].Actions.Add((action, press));
        }
        return groups;
    }
    /// <summary>Y inverted (right stick and mouse camera), every session. Off by default (Kurt, 2026-11-06: most players won't want it).</summary>
    public bool InvertByDefault { get; set; }

    public const double ZoomMin = 0.1, ZoomMax = 5;

    public static readonly string[] PressTypes = ["", "Short", "Long", "Double"];
    public const float LongPressSeconds = 1f, DoublePressSeconds = 0.3f;

    /// <summary>Extra wheel steps past the closest / farthest normal zoom step (0 = the game's limits).</summary>
    public int ZoomStepsCloser { get; set; } = 15;
    public int ZoomStepsFarther { get; set; } = 10;
    /// <summary>How much each extra step changes the distance, in percent.</summary>
    public int ZoomStepPercent { get; set; } = 15;

    /// <summary>Tilt limits in degrees (down = looking down from above).</summary>
    public int TiltDown { get; set; } = 82;
    public int TiltUp { get; set; } = 22;
    /// <summary>Orbit speed in percent of Freecam's.</summary>
    public int OrbitSpeed { get; set; } = 100;

    /// <summary>Behind mode: the look-at point moved right (units), how fast the camera follows the hero's facing (per second ×10), its tilt when switched on (degrees down).</summary>
    public int ShoulderOffset { get; set; } = 0;
    public int FollowSpeed { get; set; } = 50;
    public int BehindTilt { get; set; } = 10;

    /// <summary>Snap Behind and Selfie: the tilt they set (degrees down), Freecam's camera height (±250), and the zoom:
    /// kept as it is, or set (the game's closest step, then this many extra steps; negative = closer).</summary>
    public int SnapTilt { get; set; } = 20;
    public int SnapHeight { get; set; } = 50;
    public bool SnapKeepZoom { get; set; } = false;
    /// <summary>The set zoom as a camera zoom: steps closer than the game's closest view (negative = farther).</summary>
    public double SnapZoomX { get; set; } = 0.5;
    public int SelfieTilt { get; set; } = 5;
    public int SelfieHeight { get; set; } = -50;
    public bool SelfieKeepZoom { get; set; } = false;
    public double SelfieZoomX { get; set; } = 1.5;
    /// <summary>0.60.417–0.60.421's set zoom in whole steps closer: read from an older settings.json once, as a factor.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SnapZoomIn { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SelfieZoomIn { get; set; }

    /// <summary>A zoom factor in extra wheel steps (closer positive; fractional between steps).</summary>
    public double StepsFor(double factor) => Math.Log(factor) / Math.Log(1 + ZoomStepPercent / 100.0);
    double FactorOf(int stepsIn) => Math.Round(Math.Pow(1 + ZoomStepPercent / 100.0, stepsIn) * 20) / 20;
    /// <summary>0.60.416's zoom (steps farther, the opposite sign): read from an older settings.json once.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SnapZoom { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SelfieZoom { get; set; }

    /// <summary>Overhead labels hidden (MarvelHUD_SF.upk, HudLabels): names, titles under them, guild names.</summary>
    public bool HideNames { get; set; }
    public bool HideTitles { get; set; }
    public bool HideGuilds { get; set; }

    public static string Folder => Path.Combine(Settings.Home, "freecam");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static FreeCamSettings Load()
    {
        FreeCamSettings s = new();
        try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<FreeCamSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (JsonException) { }
        if (!string.IsNullOrEmpty(s.PadButton) && s.ModePad.Length == 0 && s.SnapPad.Length == 0) { s.ModePad = s.PadButton; s.SnapPad = s.PadButton; }
        s.PadButton = null;
        if (s.SnapZoom is int sz) s.SnapZoomIn = -sz;
        if (s.SelfieZoom is int fz) s.SelfieZoomIn = -fz;
        s.SnapZoom = null; s.SelfieZoom = null;
        if (s.SnapZoomIn is int si) s.SnapZoomX = s.FactorOf(si);
        if (s.SelfieZoomIn is int fi) s.SelfieZoomX = s.FactorOf(fi);
        s.SnapZoomIn = null; s.SelfieZoomIn = null;
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public FreeCamSettings Copy() => JsonSerializer.Deserialize<FreeCamSettings>(JsonSerializer.Serialize(this))!;

    /// <summary>Values clamped to what the game code can take.</summary>
    public void Clamp()
    {
        ZoomStepsCloser = Math.Clamp(ZoomStepsCloser, 0, 20);
        ZoomStepsFarther = Math.Clamp(ZoomStepsFarther, 0, 20);
        ZoomStepPercent = Math.Clamp(ZoomStepPercent, 5, 40);
        TiltDown = Math.Clamp(TiltDown, 10, 89);
        TiltUp = Math.Clamp(TiltUp, 0, 60);
        OrbitSpeed = Math.Clamp(OrbitSpeed, 25, 300);
        ShoulderOffset = Math.Clamp(ShoulderOffset, -150, 150);
        FollowSpeed = Math.Clamp(FollowSpeed, 5, 200);
        BehindTilt = Math.Clamp(BehindTilt, -20, 60);
        SnapTilt = Math.Clamp(SnapTilt, -20, 60); SelfieTilt = Math.Clamp(SelfieTilt, -20, 60);
        SnapHeight = Math.Clamp(SnapHeight, -250, 250); SelfieHeight = Math.Clamp(SelfieHeight, -250, 250);
        SnapZoomX = Math.Clamp(SnapZoomX, ZoomMin, ZoomMax); SelfieZoomX = Math.Clamp(SelfieZoomX, ZoomMin, ZoomMax);
    }

    /// <summary>The parts that go into MarvelGame.upk (a change there needs a rebuild; key changes only rewrite the .ini).</summary>
    public string PackageKey() => $"{InvertByDefault}|{ZoomStepsCloser}|{ZoomStepsFarther}|{ZoomStepPercent}|{TiltDown}|{TiltUp}|{OrbitSpeed}|{ShoulderOffset}|{FollowSpeed}|{BehindTilt}";
}
