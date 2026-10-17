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

    /// <summary>The Powers tab's Borrowed Look (a power taking another hero's power look): tabled 2026-10-11 (Kurt: user bugs
    /// first; the census found copy gaps, see PowerBorrow.cs), only with "PreviewFeatures": true. A mod that has borrows
    /// still rebuilds them at Save. Releasing it: set this, uncomment the manual's two Borrowed Look parts, add the
    /// changelog lines and a notice.</summary>
    public static bool PowerBorrow => PowerBorrowReleased || Settings.Load().PreviewFeatures;
    public const bool PowerBorrowReleased = false;

    public sealed record Notice(string Id, string Version, string Text, string Anchor);

    static readonly string nl = Environment.NewLine;

    /// <summary>The Discord channels for bug reports (Kurt, 2026-10-02): MHServerEmu Development and TAHITI.</summary>
    const string Discord1 = "https://discord.com/channels/1130836076332863580/1553841408471867442";
    const string Discord2 = "https://discord.com/channels/1142968916205903942/1553842238969352263";

    public static IReadOnlyList<Notice> Notices => ModelTabReleased ? [VoiceEffects(), DarkPowers(), SafeUnique(), UnibeamAndShared(), CrashChecks(), AddRigs(), PropTargets(), AdjustColors(), TidyData(), NexusSignIn(), PhoenixEffects(), AnyTarget(), TextureManager(), HistorySmall(), PowersPlaced(), ScreenFit(), FormatsAndEditors(), SizeAndNpcs(), ModelNotice(), SingleColors(), Editor(PowersAndAnimations)] : [SingleColors(), Editor(PowersAndAnimations)];

    /// <summary>0.37.291 (Kurt, 2026-10-17): voice effects, team-up voices.</summary>
    static Notice VoiceEffects() => new(
        "0.37.291-voice-effects", "0.37.291",
        "[!]Voice effects: Editor → Audio → Voice has an Effects row under Voice Shift: Reverb, Echo, Distortion, Lo-Fi, Chorus and Robot, each from Off to 100 %." + nl +
        "  • They combine with each other and with the shift; ▶ plays any line with them." + nl +
        "  • Apply to This Voice (was Shift This Voice) puts them into the mod, as a sound pack MHModManager can install too." + nl +
        "  • No Effects turns them all off." + nl + nl +
        "Team-ups have the Voice tab too: List, play, turn off or shift a team-up's lines, or give it another voice. Use Another Voice has a Team-Ups submenu, so a team-up's voice can go into a costume as well." + nl + nl +
        "Fixed: Cable's and Symbiote Spider-Man's voices couldn't be copied (\"Voice Not Changed\"). All of the game's voice sets now copy." + nl + nl +
        "Dark powers: Soft glows now fall off steeply like beams instead of showing as large dark squares." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-voice");

    /// <summary>0.37.287 (Kurt, 2026-10-16): dark powers, removing several mods at once.</summary>
    static Notice DarkPowers() => new(
        "0.37.287-dark-powers", "0.37.287",
        "[!]Dark powers: Powers tab → Dark makes a power dark (Saturation 0 %, Brightness 0 %, Opacity 500 %; the sliders fine-tune it). Glows, beams and sparks that the game adds as light are redrawn with one of the game's translucent effect materials, so they darken instead of vanishing. Tested in game on Nova's Magneton Pulsar, Nova Beam and Minor Beam." + nl + nl +
        "Opacity now goes up to 1000 %: above 100 % the effects are more solid. The 3D preview shows dark effects as the game draws them." + nl + nl +
        "Remove several mods at once: mark them (Ctrl / Shift + click), then Remove or Del. A warning in red lists every mod that will go, and Cancel is the default button." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "dark");

    /// <summary>0.37.277 (Kurt, 2026-10-14): safer Make Unique, Undo, SHARED, mods checked together, recolor black planes fixed.</summary>
    static Notice SafeUnique() => new(
        "0.37.277-safe-unique", "0.37.277",
        "[!]Power colors no longer cause black planes in game: Effects that take their materials from another game file (Nova's Pulsar powers on Richard Rider) kept them, but the recolor moved them. Open a recolored mod in the Editor and save it once to rebuild it." + nl + nl +
        "Make Unique is safer:" + nl +
        "  • A hero's own illusions, afterimages and summons (Jean Grey's and Emma Frost's illusions) stay shared, so they keep the mod's look, unless the mod added bones." + nl +
        "  • Its window says what it changes before it does anything." + nl +
        "  • Right-click → Undo Make Unique puts a mod back (for mods made unique from now on). A blue SHARED pill marks mods that share on purpose." + nl + nl +
        "Apply Changes now checks your turned-on mods together: Models in copies with different bone counts (a crash risk) and models two mods both changed are listed under Checks." + nl + nl +
        "Popups are easier to read: Sections, bullet lists and white space." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "shared");

    /// <summary>0.37.272 (Kurt, 2026-10-13): same-named powers, Add Package, Keep Shared Paths.</summary>
    static Notice UnibeamAndShared() => new(
        "0.37.272-shared", "0.37.272",
        "Power colors now reach every version of a power with the same name: Iron Man's plain Unibeam kept the game's colors (only its upgraded version changed), and Thor's Ragnarok leap was missed the same way. Re-save a mod that recolors such a power to update it." + nl + nl +
        "Powers tab: Add Package adds one of the game's effect packages to the list under its file name, for a power that isn't listed. Its color recolors the whole package, for every character that uses it." + nl + nl +
        "Editor → Overview: Keep Shared Paths, for mods that want to share the game's paths (a modded S.H.I.E.L.D. agent that should also change Nick Fury's summons). Save then doesn't make the mod unique. The note under it says what each choice does." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "keepshared");

    /// <summary>0.37.270 (Kurt, 2026-10-12): crash checks, OVERLAP, one 3D preview, material slots, hero Own Effects.</summary>
    static Notice CrashChecks() => new(
        "0.37.271-crash-checks", "0.37.271",
        "[!]A red CRASH RISK pill now marks mods whose models can crash the game (the Array.h:575 error): added bones (a cape, dress, wings or loincloth) on a model the game also keeps elsewhere with fewer bones, or a mirror table that doesn't match the bones. Click it: Make Unique and Fix Mirror Tables fix them in the mod." + nl + nl +
        "A yellow OVERLAP pill marks mods that change a model, effect or texture other game files share (the game shows whichever loads first, so near other players or NPCs the mod's look can be lost), and power packages only enemies use. Click it to Make Unique. Mods saved in the editor are made unique by themselves. The list's Group ▾ has Overlap and Conflicts." + nl + nl +
        "One 3D preview everywhere (Mods, Powers, Animations and Model tabs): the same controls, power buttons for every power (also effect-only ones such as The Will of Doom), the material changes powers make (Doom's force field), effects on props (Ghost Rider's bike wheels), bows drawn to the hand, and Look ▾ → Effects to make faint effects stronger." + nl + nl +
        "Model tab, Materials: Slot, Game Material ▾ and Own Material for models with several materials; an FBX's textures are found wherever it points, or read from inside it." + nl + nl +
        "Powers tab: Ghost Rider's flaming skull (and other heroes' always-on effects) can be recolored or hidden with Own Effects." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "crash");

    /// <summary>0.37.221 (Kurt, 2026-10-10): Add Rigs ▾ (coats, dresses, loincloths, tails, chains, wings).</summary>
    static Notice AddRigs() => new(
        "0.37.221-add-rigs", "0.37.221",
        "Model tab: Add Rigs ▾ (on the Parts tab, in place of the Cape and Hair lists) gives a model moving parts its target hero has no bones for: a cape, long hair, a coat, a dress, a loincloth, a tail, chains or wings. The model's own coat tails, skirt, strips or wings ride the added bones and move in every animation, with motion from heroes whose part is hand-animated (Blade's coat, Angela's dress, Iron Fist's loincloth, Nightcrawler's tail, Angel's wings …). Several can be on at once." + nl + nl +
        "MFF swords on Kitty Pryde's sword bones (and Ikaris', Kid Omega's, Red Guardian's) start unticked instead of floating beside the body." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-rigs");

    /// <summary>0.37.219 (Kurt, 2026-10-08): weapons and shields as Model targets, costumes that show their hero's model,
    /// better default parts, the Models Folder.</summary>
    static Notice PropTargets() => new(
        "0.37.219-prop-targets", "0.37.219",
        "Model tab: swap a costume's weapon or shield. Model ▾ (was Character ▾) marks a package's props: pick Captain America's shield and an MFF character's shield, hammer or gun takes its place, laid over the game's prop and held where the game holds it. Body and prop can both be built into one package." + nl + nl +
        "Costumes that show their hero's model instead of one of their own (Captain America Avengers, Carnage Classic, Venom Classic, Thing Classic and 16 more) work as targets now: the costume gets its own copy, so the hero's other costumes stay as they are." + nl + nl +
        "MFF characters start with their own body ticked: weapons on weapon bones (Red She-Hulk, Blade), She-Hulk's book, horses and other things that move on their own are props, unticked. Settings → Model → Change Models Folder sets where Browse for a Model opens." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-prop");

    /// <summary>0.37.216 (Kurt, 2026-10-07): Adjust Colors, gentler normal maps, building over an earlier build.</summary>
    static Notice AdjustColors() => new(
        "0.37.216-adjust-colors", "0.37.216",
        "Model tab, Materials: Adjust Colors (the sun) changes a color map's hue, saturation and brightness and its levels, with the map before and after side by side, no image editor needed. It works on a source model's maps and on the textures already in a package." + nl + nl +
        "The normal maps the importer makes from a color map are much gentler now, close to GIMP's Normal Map filter (thanks to Miike79, who compared them). Building onto a package that already holds a Model build works too." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-adjust");

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
