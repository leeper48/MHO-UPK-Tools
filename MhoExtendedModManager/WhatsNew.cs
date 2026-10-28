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

    /// <summary>The Model tab's summons and power models (From the Game lists, the Source's summons panel, the Target's Summons
    /// toggle, building a summon into its copies): parked 2026-10-26 (Kurt: another feature first), only with
    /// "PreviewFeatures": true. Off, the summon list is empty and the tab is as before summons. Releasing it: set this,
    /// uncomment the manual's summons parts (marked SUMMONS), and add a notice.</summary>
    public static bool Summons => SummonsReleased || Settings.Load().PreviewFeatures;
    public const bool SummonsReleased = true;   // (Kurt, 2026-10-28: released with the props)

    public sealed record Notice(string Id, string Version, string Text, string Anchor);

    static readonly string nl = Environment.NewLine;

    /// <summary>The Discord channels for bug reports (Kurt, 2026-10-02): MHServerEmu Development and TAHITI.</summary>
    const string Discord1 = "https://discord.com/channels/1130836076332863580/1553841408471867442";
    const string Discord2 = "https://discord.com/channels/1142968916205903942/1553842238969352263";

    public static IReadOnlyList<Notice> Notices => ModelTabReleased ? [ListsAndSpec(), MaterialCopy(), PropsAndGizmo(), SnapshotsAndEffects(), PaintAndShortcuts(), PropsAndViews(), GameSourceFeet(), RemoveCleans(), SoundsAndForms(), FormsAndSummons(), PartsAndGhosts(), VoiceEffects(), DarkPowers(), SafeUnique(), UnibeamAndShared(), CrashChecks(), AddRigs(), PropTargets(), AdjustColors(), TidyData(), NexusSignIn(), PhoenixEffects(), AnyTarget(), TextureManager(), HistorySmall(), PowersPlaced(), ScreenFit(), FormatsAndEditors(), SizeAndNpcs(), ModelNotice(), SingleColors(), Editor(PowersAndAnimations)] : [SingleColors(), Editor(PowersAndAnimations)];

    /// <summary>0.60.239 (Kurt, 2026-10-28): the empty game lists, the spec conversion, the material copy's maps.</summary>
    static Notice ListsAndSpec() => new(
        "0.60.239-lists-spec", "0.60.239",
        "[!]Model tab: the game's character lists load again for everyone." + nl +
        "  • Heroes, Team-Ups and NPCs came up empty when the MFF folder setting couldn't be read (blank, or not a usable path). The MFF folder is optional: the game's lists load without it." + nl + nl +
        "[!]Copy Setup From a Costume:" + nl +
        "  • Copy and Convert My Spec Map: a costume with an older spec map keeps its shine and reflections with a newer material (Luke Cage's Skrull with Modern's look)." + nl +
        "  • The material lists show each material's color map." + nl +
        "  • Fixed: a copy onto another hero could point at maps in a package that hero doesn't load (default textures in game). The costume's own maps are used, and the source's reflection image is copied in." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-matcopy");

    /// <summary>0.60.235 (Kurt, 2026-10-28): Copy Setup From a Costume, Current Modded Game, the animation-edit fix.</summary>
    static Notice MaterialCopy() => new(
        "0.60.235-material-copy", "0.60.235",
        "[!]Model tab → Materials: give a costume another costume's material setup." + nl +
        "  • Select a material, then This Material ▾ → Copy Setup From a Costume, and pick the costume (type to search)." + nl +
        "  • Its shader, switches and spec / rim / glow / color values are copied; the costume keeps its own maps (Luke Cage's Skrull with Modern's look)." + nl + nl +
        "[!]Source: Default Game or Current Modded Game." + nl +
        "  • Current Modded Game reads each costume as your mod list has it (the highest enabled mod's copy), with that mod's portrait and name on the row." + nl + nl +
        "Also on the Model tab:" + nl +
        "  • Animations edited in Blender no longer spin or twist in game. Build a mod made with one again to fix it." + nl +
        "  • The game's character lists no longer come up empty until the MFF folder is changed." + nl +
        "  • Ctrl+Z takes back a source picked by mistake; Ghost Rider's bike and chains are listed; the barrel keeps its size; Slot Up / Down are arrows; a • in front marks a hero's default costume." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-matcopy");

    /// <summary>0.60.219 (Kurt, 2026-10-28): props on the Model tab (Props view, gizmo, MFF weapons, hero props), summons, Variations.</summary>
    static Notice PropsAndGizmo() => new(
        "0.60.219-props-gizmo", "0.60.219",
        "[!]Model tab → Props: give a costume props from anywhere." + nl +
        "  • Pick a character on the left (From the Game, an MFF character or an FBX): its props are listed under it." + nl +
        "  • Left-click a prop to see it on its own; right-click it to add it to the costume under Target. The target never changes there." + nl +
        "  • From the Game lists a costume's props, summons and power models (Squirrel Girl's squirrels); an MFF character lists its weapons first." + nl +
        "  • An MFF weapon goes on the hand that holds it, sized to the hero." + nl + nl +
        "[!]A move / turn gizmo on the prop in the preview (X red, Y green, Z blue, lined up with the world)." + nl +
        "  • Drag an arrow to move it, a ring to turn it (5° steps, Shift: 1°). Let go and the fit goes in." + nl +
        "  • It works on added props and on a costume's own (Thor's hammer). The Gizmo button beside Reset Fit hides it." + nl + nl +
        "Also on the Model tab:" + nl +
        "  • Right-click a costume under Target: Revert to Original, or Revert to Last Build." + nl +
        "  • The Target's Props lists the props a costume gets from its hero (Doom's pistol and throne): untick one to leave it off." + nl +
        "  • Summons and power models can be targets and sources (Cable's illusions, Strike Team's Beast); Variations sets the random materials a character picks from." + nl + nl +
        "Settings → File Pickers: choose the view Open windows start in, for packages, images and 3D models." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-addprop");

    /// <summary>0.50.16 (Kurt, 2026-10-19): forms, transforms and summons in the 3D preview; Generate Normal Map; normal maps sRGB off.</summary>
    /// <summary>0.60.143 (Kurt, 2026-10-25): weight painting, keyboard shortcuts, fit fixes, Nexus sign-in.</summary>
    /// <summary>0.60.164 (Kurt, 2026-10-26): Snapshots, bundles and discarded copies; Effect Models; Model tab lists.</summary>
    static Notice SnapshotsAndEffects() => new(
        "0.60.164-snapshots-effects", "0.60.164",
        "[!]Settings → Snapshots: what changed, and a way back." + nl +
        "  • A snapshot is taken at every start, before every Apply, and when you click the camera: which mods are on, their order, versions, Make Unique, Crash Risk fixes, OVERLAP and CRASH RISK warnings, and the game files Apply wrote." + nl +
        "  • Compare with Now lists everything that changed since: the first place to look when the game crashes now and didn't before." + nl +
        "  • Restore puts it all back, mods' files too: removed mods return; edits, updates, Make Unique and Fix Mirror Tables are undone." + nl +
        "  • Bundles: every mod (or the turned-on ones) and its state in one .zip, to keep a set that works or move it to another PC." + nl +
        "  • Replaced and removed mods now go to a discarded folder (kept 30 days) instead of the Recycle Bin, so Restore can bring them back." + nl + nl +
        "[!]Model tab → Effects: the models powers throw around (Squirrel Girl's squirrels, acorns, Hulk's asteroid …). Edit in Blender or Import FBX, then Build into Mod puts it in every package that carries it." + nl + nl +
        "Model tab:" + nl +
        "  • From the Game has Heroes, Team-Ups and NPCs tabs; the Target has a dropdown like the Source's." + nl +
        "  • Right-click a source: the Target jumps to the same character." + nl +
        "  • Edit in Blender beside Build: the same scene opens again; Build When Blender Saves builds on each Ctrl+S." + nl +
        "  • Lean works on FBX and game models too." + nl + nl +
        "Also: Make Unique and the crash checks now cover powers' projectiles, hotspots and conditions." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-snapshots");

    static Notice PaintAndShortcuts() => new(
        "0.60.143-paint-shortcuts", "0.60.143",
        "[!]Model tab → Bone Map → Paint: weight painting, as Blender's." + nl +
        "  • Draw, Add, Subtract and Blur brushes with Weight, Strength and Radius; Shift blurs, Ctrl paints the other way, F and Shift+F change the radius and strength." + nl +
        "  • Mirror, Invert Bone and Smooth Bone; the brush reaches every part it touches. [ and ] step up and down the bones." + nl +
        "  • W (or the Weights button): off, the weights over the model at 50 %, the weights alone." + nl +
        "  • History (beside Undo / Redo) lists every step by name; undoing paint is instant." + nl + nl +
        "[!]Settings → Keyboard Shortcuts: change any shortcut or 3D mouse binding (conflicts show in red). Right-click any button to give it a shortcut. Tooltips show the keys." + nl + nl +
        "Model tab fixes:" + nl +
        "  • Lean starts at an automatic amount and is spread over the ankles and hips (no more ankle creases)." + nl +
        "  • A mouth bone that isn't named like a jaw is found by where it sits (Sharon Carter's mouth no longer opens with Black Widow's bangs)." + nl +
        "  • Team-ups whose animations shorten a limb no longer crease at the joint." + nl +
        "  • Game Joint Blend (test, off by default): the skin at hips, knees, shoulders and elbows shared as the game's own model does." + nl + nl +
        "Also: Sign In with Nexus has a window to paste the code when the browser can't reach the app; Group ▾ → Crash Risk." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "model");

    /// <summary>0.60.122 (Kurt, 2026-10-24): props from other characters, 3D view tools, Duplicate, Model tab fixes.</summary>
    static Notice PropsAndViews() => new(
        "0.60.122-props-views", "0.60.122",
        "[!]Animations tab: Props → Add a Prop gives a costume another character's prop (Thor's hammer for Captain America), held in every animation." + nl +
        "  • Size, Move and Turn fit it. Pivot on the Hand holds it in the other character's animations the way that character does (the hammer swings by its strap in Thor's fidgets)." + nl +
        "  • In This Animation moves and turns it further in one animation only." + nl + nl +
        "[!]3D views:" + nl +
        "  • An axis gizmo (X red, Y green, Z blue): click a ball to look along that axis." + nl +
        "  • Quad View (Shift+Q): the camera plus Top, Front and Side." + nl +
        "  • Look ▾ → Barrel Size Reference (an oil drum to judge sizes by: drag it, double-click to put it back) and Prop Pivots." + nl +
        "  • An amber warning when the animation playing won't play in game for this costume, with how to fix it." + nl + nl +
        "Mods tab: Right-click a card → Duplicate makes a copy of a mod to experiment from." + nl + nl +
        "Model tab: A Lean slider under Feet; models with two spine bones no longer lean forward; the preview and Blender export play the mod's own animation changes; animations edited in Blender no longer turn the body in game, and repeated Builds no longer pile up copies." + nl + nl +
        "Also: Click a slider's number to type a value; Ctrl+S saves and keeps editing, Ctrl+Enter saves and goes back; another hero's animations are fitted to the costume's skeleton; Apply Changes no longer undoes an extra icon package every second time." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "animations");

    /// <summary>0.60.75 (Kurt, 2026-10-23): Model tab From the Game source, feet and size; effects in Create from 3D.</summary>
    static Notice GameSourceFeet() => new(
        "0.60.75-game-source-feet", "0.60.75",
        "[!]Model tab: Source → From the Game: any hero costume, team-up or NPC from the game can be the model, with its own textures, fitted onto the target." + nl +
        "  • Source and Target both start on From the Game, and the list you pick is remembered." + nl +
        "  • A • after a name marks the hero's default costume, as the game has it (after reworks and visual updates that's often not the one named Base or Classic)." + nl + nl +
        "[!]Model tab: A Feet slider puts a model's feet on the floor in game." + nl +
        "  • It starts at an automatic amount, measured on the model in the hero's idle. If the feet still float or sink, move it; double-click goes back to automatic." + nl +
        "  • Keep the Source's Size keeps an MFF model at its own size instead of the target's height (Hulk stays Hulk-sized on Black Widow)." + nl +
        "  • B shows the bones, W the weights." + nl + nl +
        "Create from 3D: an Effects button shows the character's own effects (Ghost Rider's burning head) in the view and the icon, and Thirds / Center / Safe Area guides help framing. The 3D previews play those effects with every animation." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "model");

    /// <summary>0.60.59 (Kurt, 2026-10-22): a user's report on a costume's voice shift.</summary>
    static Notice RemoveCleans() => new(
        "0.60.59-remove-cleans", "0.60.59",
        "[!]Editor: Removing a package from a mod also removes its voice shift and its sound pack." + nl +
        "  • Before, a removed costume's shift stayed in the mod, and a new shift's lines clashed with it (\"158 skipped\")." + nl + nl +
        "A sound file put back to the game's own gets the game's date again, like packages, so it doesn't look changed." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "voice");

    /// <summary>0.60.58 (Kurt, 2026-10-22): users' reports: sound packs after a Steam verify, a costume's voice shift, Jean's form animations.</summary>
    static Notice SoundsAndForms() => new(
        "0.60.58-sounds-forms", "0.60.58",
        "[!]Sound packs work again after a Steam verify:" + nl +
        "  • A verify puts the game's sound files back unchanged but newly dated, and the app skipped them (\"no original\"), so sound packs and voice shifts didn't reach the game and Remove Mod refused." + nl +
        "  • The app now knows all 227 stock sound files by their contents: Apply Changes rebuilds them." + nl + nl +
        "[!]Voice tab: In a mod for one of a hero's costumes, Use <voice> puts the hero's voice into that costume's own package, so a Voice Shift changes that costume." + nl +
        "  • Before, it added the hero's default costume and shifted that one (White Tiger on Iron Fist Classic kept his voice)." + nl +
        "  • A mod made that way: remove the default costume's package from the mod, then Use <voice> and shift again." + nl + nl +
        "Animations tab: A hero's form animations are listed too (Jean Grey's flying Phoenix ones, marked Form), each with its form's powers, and play their own version in the 3D view." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "animations");

    static Notice FormsAndSummons() => new(
        "0.50.16-forms-summons", "0.50.16",
        "[!]3D previews show forms, transforms and summons:" + nl +
        "  • Jean Grey's Phoenix form: her flying animations, wings and flames, and buttons for the form's own powers." + nl +
        "  • Rogue's Final Form, Rocket's Mechfall and Loki's Frost Giant show the transformed character." + nl +
        "  • Summoned characters stand beside the hero: Jean's illusions (see-through with the game's glow), Doctor Strange's images, Nick Fury's agents, Squirrel Girl's squirrels." + nl + nl +
        "[!]Model tab → Materials: Right-click a Color or Normal row → Generate Normal Map (Soft, Medium, Strong)." + nl +
        "  • Every normal map the app writes now has sRGB off, as the game's own do. That fixes seams across faces." + nl +
        "  • A model built before this: Build into Mod again." + nl + nl +
        "Model tab: It opens on the mod's character. While filtering the targets, heroes come first, then team-ups, then NPCs." + nl + nl +
        "3D previews play smoother: up to about 60 frames a second." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-forms");

    /// <summary>0.50.7 (Kurt, 2026-10-18): parts, dodge ghosts, the editor's new layout, the target's props, a faster editor.</summary>
    static Notice PartsAndGhosts() => new(
        "0.50.7-parts-ghosts", "0.50.7",
        "[!]Powers tab → Parts: Switch off a power's or a hero's settings (Invisible Woman's hover when she runs), attachments, effects and sounds. ▶ shows an effect or plays a sound. The Char tile lists the hero's own parts, and its Color recolors them." + nl + nl +
        "[!]Dodge ghosts from your model: The Dodge tile → Make Them From This Model." + nl +
        "  • Your model at its own size, in the combat idle, each ghost leaning another way (the Lean slider sets how far)." + nl +
        "  • Use This Pose takes any animation's frame; every costume of the hero shows them." + nl + nl +
        "Model tab → Parts: The Target's Props lists the target costume's own props (Black Cat's belt). Untick one to leave it off." + nl + nl +
        "The Editor has more room:" + nl +
        "  • Name, author, version, tags and note are in Overview → Details." + nl +
        "  • Powers: icon tiles under a much bigger 3D view." + nl +
        "  • Save and Keep Editing stays where you are, with no reload; a dot after the name means unsaved changes." + nl + nl +
        "3D views: A hexagon floor (Look ▾ → Ground). Picking a power frames the whole character." + nl + nl +
        "Faster over a long session: Closed editors no longer stay in memory." + nl + nl +
        "If something doesn't work, please report it on Discord: [MHServerEmu Development](" + Discord1 + ") or [TAHITI](" + Discord2 + ").",
        "w-parts");

    /// <summary>0.37.292 (Kurt, 2026-10-17): voice effects, team-up voices, moved costumes that hung the game.</summary>
    static Notice VoiceEffects() => new(
        "0.37.292-voice-effects", "0.37.292",
        "[!]Voice effects: Editor → Audio → Voice has an Effects row under Voice Shift: Reverb, Echo, Distortion, Lo-Fi, Chorus and Robot, each from Off to 100 %." + nl +
        "  • They combine with each other and with the shift; ▶ plays any line with them." + nl +
        "  • Apply to This Voice (was Shift This Voice) puts them into the mod, as a sound pack MHModManager can install too." + nl +
        "  • No Effects turns them all off." + nl + nl +
        "Team-ups have the Voice tab too: List, play, turn off or shift a team-up's lines, or give it another voice. Use Another Voice has a Team-Ups submenu, so a team-up's voice can go into a costume as well." + nl + nl +
        "Fixed: Cable's and Symbiote Spider-Man's voices couldn't be copied (\"Voice Not Changed\"). All of the game's voice sets now copy." + nl + nl +
        "[!]Fixed: A moved costume could hang the game, then crash it (Emma Frost New Tian on Old Man Logan, in Diamond Form). Some of its textures got a path the game's texture cache doesn't know. Moves now keep those paths." + nl +
        "  • A copy moved with an older version shows a red CRASH RISK pill. Move the original again and choose Rebuild It, then Apply Changes." + nl + nl +
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
