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
    /// <summary>How each action's button is pressed: "" (none), "Short" (a tap), "Long" (held the long-press time),
    /// "Double" (two taps), "ShortLong" (a tap, then the second press held the long-press time).</summary>
    public string ModePress { get; set; } = "Short";
    public string SnapPress { get; set; } = "Long";
    /// <summary>0.60.410–0.60.412's one shared button: read from an older settings.json into both actions.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? PadButton { get; set; }

    /// <summary>Selfie: like Snap Behind, from the front (the camera faces the hero).</summary>
    public string SelfieKey { get; set; } = "F3";
    public string SelfiePad { get; set; } = "";
    public string SelfiePress { get; set; } = "";

    /// <summary>One mode each (Kurt, 2026-11-09): Locked Behind and Default MHO, beside the cycle (Free Orbit is Snap Behind's:
    /// it switches to Free Orbit). No keys by default.</summary>
    public string BehindKey { get; set; } = "";
    public string BehindPad { get; set; } = "";
    public string BehindPress { get; set; } = "";
    public string IsoKey { get; set; } = "";
    public string IsoPad { get; set; } = "";
    public string IsoPress { get; set; } = "";

    /// <summary>The actions in order: name for the game code, keyboard spec, gamepad button, press.</summary>
    public (string Action, string Key, string Pad, string Press)[] Actions() =>
        [("Mode", ModeKey, ModePad, ModePress), ("Snap", SnapKey, SnapPad, SnapPress), ("Selfie", SelfieKey, SelfiePad, SelfiePress),
         ("Behind", BehindKey, BehindPad, BehindPress), ("Isometric", IsoKey, IsoPad, IsoPress)];

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

    public static readonly string[] PressTypes = ["", "Short", "Long", "Double", "ShortLong"];
    public const float DoublePressSeconds = 0.3f;
    /// <summary>How long a gamepad button is held for a long press, in milliseconds (Kurt, 2026-11-09: was a fixed second).</summary>
    public int LongPressMs { get; set; } = 1000;
    /// <summary>The camera mode, zoom and angle kept between sessions (and zone changes): saved by the game's script in
    /// its own settings file, put back when the camera is made.</summary>
    public bool RememberCamera { get; set; } = true;

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
    /// <summary>Default MHO mode (Kurt, 2026-11-09): the game's own camera direction, with its tilt kept (DefaultGameTilt) or
    /// set, and the height and zoom it starts at (the game's zoom, or set as Snap Behind's is). Every default is the base game's.</summary>
    public bool DefaultGameTilt { get; set; } = true;
    public int DefaultTilt { get; set; } = 45;
    public int DefaultHeight { get; set; } = 0;
    /// <summary>Default MHO keeps the zoom it's switched on at (Keep Current In-Game Zoom, as on Snap Behind; until 0.60.450 "Game's
    /// Zoom", which also took the extra wheel steps off), or starts at DefaultZoomX.</summary>
    public bool DefaultGameZoom { get; set; } = true;
    /// <summary>Default MHO's set zoom, a factor of the game's closest distance like Snap Behind's (0.5 = twice as far). The
    /// game's own step nearest that distance is used (its angle and height), extra wheel steps only past its farthest step.</summary>
    public double DefaultZoomX { get; set; } = 1.0;
    /// <summary>Locked Behind's zoom when it's switched on (Kurt, 2026-11-09): kept, or set as Snap Behind's is.</summary>
    public bool BehindKeepZoom { get; set; } = true;
    /// <summary>Locked Behind holds its Start Tilt (the stick and mouse camera don't tilt it; Kurt, 2026-11-09).</summary>
    public bool BehindLockTilt { get; set; } = false;
    public double BehindZoomX { get; set; } = 0.5;
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

    /// <summary>Permanent Wall Fade: interior walls fade (the game's own way) instead of Freecam 1.2's always visible, and F11
    /// no longer switches them. Off by default so F11 keeps working (Kurt, 2026-11-09). Freecam 1.0 / 1.1 have no override,
    /// so their walls fade anyway.</summary>
    public bool WallsFade { get; set; }
    /// <summary>Overhead labels hidden (MarvelHUD_SF.upk, HudLabels): names, titles under them, guild names.</summary>
    public bool HideNames { get; set; }
    public bool HideTitles { get; set; }
    public bool HideGuilds { get; set; }
    /// <summary>The level number (and its badge) by characters' names (MarvelInWorldEntityInfo.SetLevel).</summary>
    public bool HideLevels { get; set; }

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
        LongPressMs = Math.Clamp(LongPressMs, 200, 3000);
        DefaultTilt = Math.Clamp(DefaultTilt, 5, 89); DefaultHeight = Math.Clamp(DefaultHeight, -250, 250);
        DefaultZoomX = Math.Clamp(DefaultZoomX, ZoomMin, ZoomMax);
        BehindZoomX = Math.Clamp(BehindZoomX, ZoomMin, ZoomMax);
    }

    /// <summary>The parts that go into MarvelGame.upk (a change there needs a rebuild; key changes only rewrite the .ini).</summary>
    public string PackageKey() => $"{InvertByDefault}|{ZoomStepsCloser}|{ZoomStepsFarther}|{ZoomStepPercent}|{TiltDown}|{TiltUp}|{OrbitSpeed}|{ShoulderOffset}|{FollowSpeed}|{BehindTilt}|{LongPressMs}|{RememberCamera}|{DefaultGameTilt}|{DefaultTilt}|{DefaultHeight}|{DefaultGameZoom}|{DefaultZoomX}|{BehindKeepZoom}|{BehindZoomX}|{BehindLockTilt}|{WallsFade}";
}
