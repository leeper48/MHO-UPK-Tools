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

    public sealed record Notice(string Id, string Version, string Text, string Anchor);

    static readonly string nl = Environment.NewLine;

    /// <summary>The Discord channels for bug reports (Kurt, 2026-10-02): MHServerEmu Development and TAHITI.</summary>
    const string Discord1 = "https://discord.com/channels/1130836076332863580/1553841408471867442";
    const string Discord2 = "https://discord.com/channels/1142968916205903942/1553842238969352263";

    public static IReadOnlyList<Notice> Notices => [Editor(PowersAndAnimations)];

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
