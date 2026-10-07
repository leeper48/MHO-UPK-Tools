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
    public const bool ModelTabReleased = true;   // (Kurt, 2026-10-04: the next push is live with the Model tab)

    public sealed record Notice(string Id, string Version, string Text, string Anchor);

    static readonly string nl = Environment.NewLine;

    /// <summary>The Discord channels for bug reports (Kurt, 2026-10-02): MHServerEmu Development and TAHITI.</summary>
    const string Discord1 = "https://discord.com/channels/1130836076332863580/1553841408471867442";
    const string Discord2 = "https://discord.com/channels/1142968916205903942/1553842238969352263";

    public static IReadOnlyList<Notice> Notices => ModelTabReleased ? [TidyData(), NexusSignIn(), PhoenixEffects(), AnyTarget(), TextureManager(), HistorySmall(), PowersPlaced(), ScreenFit(), FormatsAndEditors(), SizeAndNpcs(), ModelNotice(), SingleColors(), Editor(PowersAndAnimations)] : [SingleColors(), Editor(PowersAndAnimations)];

    /// <summary>0.37.213 (Kurt, 2026-10-07): the data folder cleans up after itself; the Post folder only with a saved post.</summary>
    static Notice TidyData() => new(
        "0.37.213-tidy-data", "0.37.213",
        "A tidier data folder: each start now clears what nothing needs any more: the editor's converted images (Save already copies them into the mod), model thumbnails not shown for 30 days, Nexus downloads once installed, and the editor's leftovers from a crash. Your mods, settings and Model-tab work are kept; the manual's \"The Data Folder\" section says what's what." + nl + nl +
        "Export to ZIP adds the \"- Post\" folder beside the zip only for a mod with a saved post (Create Post → Save to Mod)." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "datafolder");

    /// <summary>0.37.212 (Kurt, 2026-10-07): Nexus registered the app: Sign In with Nexus for one-click updates.</summary>
    static Notice NexusSignIn() => new(
        "0.37.212-nexus-sign-in", "0.37.212",
        "Sign In with Nexus: Nexus Mods has approved the app. In the Nexus strip above the mod list, ▾ → Sign In with Nexus opens your browser to approve it (the app never sees your password)." + nl + nl +
        "Signed in with a Premium account, ↑ UPDATE on a mod downloads and installs its new version in one click. Without signing in everything works as before: Check for Updates finds new versions and opens their Files page." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-nexus");

    /// <summary>0.37.211 (Kurt, 2026-10-07): a hero's audio package's powers and own effects; Replace File.</summary>
    static Notice PhoenixEffects() => new(
        "0.37.211-phoenix-effects", "0.37.211",
        "What's new:" + nl + nl +
        "• Powers tab: a mod with a hero's audio package (Jean Grey's holds her Phoenix form) shows that hero's powers and the package's Own Effects to recolor, and the preview plays them (the Dark Phoenix ring of fire)." + nl + nl +
        "• Model tab, Materials: Use a File is now called Replace File." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-own");

    /// <summary>0.37.208 (Kurt, 2026-10-07): any package as a Model target, a choice of character, more textures to edit.</summary>
    static Notice AnyTarget() => new(
        "0.37.208-any-target", "0.37.208",
        "New in the Editor's Model tab:" + nl + nl +
        "• Browse for a Package (end of the Target list) makes any package with a character in it the target: a pet, a vehicle, a prop, another mod's package." + nl + nl +
        "• Character ▾ picks which character in a package the model replaces, when it holds several (Cyclops's base package: Cyclops, Angel, Wolverine's bike)." + nl + nl +
        "• Materials lists more of a model's textures to edit: those of plain materials, and those it takes from its hero's base package (Jean Grey's Phoenix wings; changing one adds that package to the mod, asked first)." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-npc");

    /// <summary>0.37.206 (Kurt, 2026-10-06): MH Texture Manager mods installed as package mods.</summary>
    static Notice TextureManager() => new(
        "0.37.206-texture-manager", "0.37.206",
        "Install Mod now takes mods made for the older MH Texture Manager (a .json and a .tfc). They're converted into ordinary mods as they install: the textures go into the game packages that use them, so the mod turns on and off like any other and the game's texture caches are left alone. The name and version come from the Nexus download's name, and the author from its Nexus page." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-install");

    /// <summary>0.37.204 (Kurt, 2026-10-06): data\history no longer fills up.</summary>
    static Notice HistorySmall() => new(
        "0.37.204-history", "0.37.204",
        "Smaller data folder: Apply no longer keeps a full copy of every game file it replaces in data\\history (nothing used them, and they grew to gigabytes). It keeps only a small record of what it wrote, so it still tells its own changes from other programs'. The old copies were deleted at this start; your mods and the game are unchanged." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "settings");

    /// <summary>0.37.203 (Kurt, 2026-10-06): power effects where the game puts them, P scrubs, remove a Model target, MFF rig fixes.</summary>
    static Notice PowersPlaced() => new(
        "0.37.203-powers-placed", "0.37.203",
        "What's new:" + nl + nl +
        "• Power effects in the 3D preview play where the game puts them, for every hero: beams reach toward a target in front of the hero at the height they aim, missiles burst where they land, and what a power does to the one it hits (stuns, burns, impacts) plays there instead of on the hero." + nl + nl +
        "• P pauses and puts the focus on the Frame slider: the arrow keys then step frame by frame." + nl + nl +
        "• Model tab: right-click a target → Remove (or Delete) takes it off the list. One the tab added leaves the mod; one of the mod's own can get its own copy back." + nl + nl +
        "• Model tab: MFF models with unusual rigs move better: helper bones at the hips and shoulders, numbered twist bones (Kamala Khan, Doctor Strange, Magik …), hands and feet exported loose (Kamala's base model, Sandman, the Sentinels), the forearm twist, and team-ups' extra limb bones. Rebuild a model to get the fixes." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ")." + nl + nl +
        "The manual's Walkthroughs show each step (Help, or F1); Settings → What's New shows this notice and the earlier ones.",
        "w-powers");

    /// <summary>0.37.192 (Kurt, 2026-10-06): UI scale, resizable and foldable panels.</summary>
    static Notice ScreenFit() => new(
        "0.37.192-screen-fit", "0.37.192",
        "Fit the app to your screen:" + nl + nl +
        "• Settings → UI Scale (80–150 %) makes the whole app's text and controls smaller or larger (from the next start)." + nl + nl +
        "• Drag the dividers: beside the mod list, between the preview and the details, and on the Model tab between Source, Preview and Target, Target's list and its tabs, and above the log." + nl + nl +
        "• Fold panels away: click a Model tab heading (Source, Target, Material, Size in Game, Log), or the ▴ next to Version in the Editor for the Tags and Note rows." + nl + nl +
        "• In any table, dragging a column divider sizes the column on its left." + nl + nl +
        "All of it is remembered. Also fixed: the Model tab's first log line is clearer, and on a scaled screen tooltips are their normal size again." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ")." + nl + nl +
        "The manual's Walkthroughs show each step (Help, or F1); Settings → What's New shows this notice and the earlier ones.",
        "w-layout");

    /// <summary>0.37.187 (Kurt, 2026-10-06): more model formats, your image editor, the busy ring, MFF folder layouts.</summary>
    static Notice FormatsAndEditors() => new(
        "0.37.187-formats-editors", "0.37.187",
        "New in the Editor's Model tab:" + nl + nl +
        "• More model files: Single Model takes OBJ, DAE, STL, Blender (.blend) and XNALara / XPS (.xps, .mesh, .mesh.ascii) as well as FBX." + nl + nl +
        "• Your image editor: Materials → the pencil (or right-click a row, or Edit in … in the large view) opens a map in GIMP, Photoshop, Corel PHOTO-PAINT and others; every save there comes back to the model. Settings → Model → Choose Image Editor picks one." + nl + nl +
        "• With no source picked, the Materials tab lists the textures of the model already in the package, to edit them right there. Ctrl+click the model to pick its material." + nl + nl +
        "• A busy ring turns while things load or build in the background (top right, and beside the Model tab's status)." + nl + nl +
        "• The MFF folder can be laid out as Models\\Models or just Models, with Texture2D or Textures." + nl + nl +
        "[!] These features are experimental. Test your builds in game before sharing them." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ")." + nl + nl +
        "The manual's Walkthroughs show each step (Help, or F1); Settings → What's New shows this notice and the earlier ones.",
        "w-textures");

    /// <summary>0.37.179 (Kurt, 2026-10-06): size in game, NPC / enemy / team-up models and effects, Blender texture paint.</summary>
    static Notice SizeAndNpcs() => new(
        "0.37.179-size-npcs", "0.37.179",
        "New in the Editor:" + nl + nl +
        "• Size in Game (Model tab): make a character bigger or smaller (5–400 %), with or without a new model. Match Steps to Size keeps the feet from sliding." + nl + nl +
        "• Models on NPCs, enemies and team-ups: From the Game lists the game's NPCs and Enemies and Bosses with the heroes' skeleton, and the team-ups." + nl + nl +
        "• Their effects in the Powers tab: an NPC's, enemy's or team-up's Own Effects (glows, trails) and its own powers can be recolored, and Opacity fades an effect out (at 0 % a hologram team-up shows its own materials)." + nl + nl +
        "• Blender: Texture Paint comes back with Ctrl+S, as the material's maps. Single Model takes any FBX with one Browse (an MFF model is recognized) and finds textures in the folders next to it." + nl + nl +
        "• Forgot to build? Leaving the Model tab or Save Changes asks to build first." + nl + nl +
        "[!] These features are experimental. Test your builds in game before sharing them." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ")." + nl + nl +
        "The manual's Walkthroughs show each step (Help, or F1); Settings → What's New shows this notice and the earlier ones.",
        "w-size");

    /// <summary>The Model tab's release (written 2026-10-04 for 0.37.153; shown once ModelTabReleased is true: give it the
    /// release's version then).</summary>
    static Notice ModelNotice() => new(
        "model-tab-release", "0.37.157",
        "New in the Editor: the Model tab." + nl + nl +
        "• Put a model of your own on a costume: a Marvel Future Fight character from your own MFF rip, or an FBX file. It's fitted to the hero's skeleton, so the hero's animations, powers and props play on it." + nl + nl +
        "• FBX files can come rigged (with MHO bone names, Mixamo names or other skeletons) or as a plain mesh: a mesh is stood up, scaled to the hero and rigged in Blender for you." + nl + nl +
        "• Materials: each map and where it comes from, your own files, the game's own packed spec maps, Tag Colors (say what each color is made of: metal, skin, leather, cloth or glow), glow maps, and Export Maps." + nl + nl +
        "• Full Export opens the model and its animations in Blender; every Ctrl+S there sends your changes back." + nl + nl +
        "Also new everywhere: buttons are icons (point at one for its name), tooltips show on grayed-out buttons and in pop-up windows, and P plays or pauses any 3D view." + nl + nl +
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
