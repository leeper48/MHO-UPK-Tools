namespace MhoExtendedModManager;

/// <summary>
/// Notices of new features (Kurt, 2026-10-02): the newest is shown once when a version first starts (MainForm.ShowWhatsNew),
/// and Settings → What's New shows them all. To announce a release, put a new notice first (a new Id shows it again);
/// keep the older ones. Anchor is the manual section Open the Manual goes to.
/// </summary>
static class WhatsNew
{
    /// <summary>
    /// The Editor's Powers (power colors) and Animations (animation swaps) tabs: released in 0.37.109 (Kurt, 2026-10-02;
    /// before that only with "PreviewFeatures": true in settings.json). False hides them again (and their part of the notice).
    /// </summary>
    public const bool PowersAndAnimationsReleased = true;

    public static bool PowersAndAnimations => PowersAndAnimationsReleased || Settings.Load().PreviewFeatures;
    /// <summary>The editor's Model tab (MFF models onto a mod's package): in development, only with "PreviewFeatures": true.</summary>
    public static bool ModelTab => ModelTabReleased || Settings.Load().PreviewFeatures;
    /// <summary>The Model tab is out for everyone (Kurt decides; then also uncomment its manual section and add a notice).</summary>
    public const bool ModelTabReleased = false;

    public sealed record Notice(string Id, string Version, string Text, string Anchor);

    static readonly string nl = Environment.NewLine;

    /// <summary>The Discord channels for bug reports (Kurt, 2026-10-02): MHServerEmu Development and TAHITI.</summary>
    const string Discord1 = "https://discord.com/channels/1130836076332863580/1553841408471867442";
    const string Discord2 = "https://discord.com/channels/1142968916205903942/1553842238969352263";

    public static IReadOnlyList<Notice> Notices => ModelTabReleased ? [ModelNotice(), SingleColors(), Editor(PowersAndAnimations)] : [SingleColors(), Editor(PowersAndAnimations)];

    /// <summary>The Model tab's release (written 2026-10-04 for 0.37.153; shown once ModelTabReleased is true: give it the
    /// release's version then).</summary>
    static Notice ModelNotice() => new(
        "model-tab-release", "0.37.153",
        "New in the Editor: the Model tab." + nl + nl +
        "• Put a model of your own on a costume: a Marvel Future Fight character from your own MFF rip, or an FBX file. It's fitted to the hero's skeleton, so the hero's animations, powers and props play on it." + nl + nl +
        "• FBX files can come rigged (with MHO bone names, Mixamo names or other skeletons) or as a plain mesh: a mesh is stood up, scaled to the hero and rigged in Blender for you." + nl + nl +
        "• Materials: each map and where it comes from, your own files, the game's own packed spec maps, Tag Colors (say what each color is made of: metal, skin, leather, cloth or glow), glow maps, and Export Maps." + nl + nl +
        "• Full Export opens the model and its animations in Blender; every Ctrl+S there sends your changes back." + nl + nl +
        "[!] This is experimental. Test your builds in game before sharing them." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ")." + nl + nl +
        "The manual explains each step (Help, or F1); Settings → What's New shows this notice and the earlier ones.",
        "model");

    /// <summary>0.37.112 (Kurt, 2026-10-03): single power colors with the color wheel and eyedropper.</summary>
    static Notice SingleColors() => new(
        "0.37.112-single-colors", "0.37.112",
        "New in the Editor's Powers tab:" + nl + nl +
        "• Change single colors: Colors in This Power shows the colors a power really uses. Click one to replace just that color (for example only the blue of Thor's lightning), keeping the rest of the power as it is. Range sets how close shades change with it." + nl + nl +
        "• Pick the new color as a hex code, on a color wheel, or with the Eyedropper: press it and drag onto any color on the screen, even in another window. Your five most recent colors are kept, and Reset goes back to the power's own color." + nl + nl +
        "• Switching powers keeps the frame slider's place, to compare powers at the same moment." + nl + nl +
        "[!] These features are experimental." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ")." + nl + nl +
        "The manual explains each step (Help, or F1); Settings → What's New shows this notice and the earlier ones.",
        "singlecolors");

    static Notice Editor(bool powers) => new(
        "0.37.109-editor" + (powers ? "-powers-animations" : ""), "0.37.109",
        "New in the Editor tab:" + nl + nl +
        (powers
            ? "• Powers: Recolor the hero's powers. Pick a power, turn Hue, Saturation and Brightness, and watch it play with its effects in the 3D preview. Save puts the recolored power files into the mod." + nl + nl +
              "• Animations: See every animation a costume plays and swap any of them for another hero's, a team-up's or another mod's (Copy From a Character swaps many at once). Only the costume's own package changes." + nl + nl
            : "") +
        "• Voice Shift (Audio → Voice): Make a costume's voice higher or lower, for example to turn a female voice male or the other way round. ▶ previews any line; Shift This Voice puts it into the mod." + nl + nl +
        "• When a mod has no voice lines, the Voice tab says where the hero's voice is in the game and adds it with one click." + nl + nl +
        "• The Editor's tabs are grouped: " + (powers ? "Overview, Icons, Strings, Audio, Powers and Animations." : "Overview, Icons, Strings and Audio.") + nl + nl +
        "[!] These features are experimental." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ")." + nl + nl +
        "Mods made with them work with the old MHModManager too. The manual explains each step (Help, or F1 at any time); this notice is in Settings → What's New.",
        powers ? "powers" : "voice");
}
