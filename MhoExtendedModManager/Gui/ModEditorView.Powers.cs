using MhoPackageModifier;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The editor's Powers tab (Kurt, 2026-10-01: a power customizer; how the MHO Hero Creator does it, without anything the
/// server has to know): the hero's powers with their icons, a color per power (hue, saturation, brightness) previewed in
/// 3D, and on Save each colored power's stock packages recolored (PowerRecolor) into the mod's packages. A color changes
/// that power for every costume of the hero: the files are the game's own power packages.
/// </summary>
sealed partial class ModEditorView
{
    StorePreview? powerPreview;
    /// <summary>The powers and other entries, as icon tiles under the 3D view (a ListBox before 0.37.300).</summary>
    readonly PowerBar powerList = new();
    /// <summary>The hero's powers come first in the bar; the entries after them (own effects, parts, packages) start a group.</summary>
    int heroPowerCount;
    /// <summary>What the Powers tab shows (Save and Keep Editing opens the editor again on it: Kurt, 2026-10-18).</summary>
    public sealed record PowersState(string Proto, string? Part, int SideTab, (string Anim, float Frame)? Pose);

    public PowersState? CapturePowersState() => powerList.SelectedItem is Fx.PowerList.Power p
        ? new PowersState(p.Prototype, p.Part, powerSideTabs?.SelectedIndex ?? 0, powerPreview?.CurrentPose is { Anim.Length: > 0 } pose ? pose : null) : null;

    /// <summary>The entry, its Color / Parts tab and the pose again, once the tab has its powers (in the background).</summary>
    public async void RestorePowersState(PowersState st)
    {
        for (int i = 0; i < 900 && !IsDisposed; i++)
        {
            if (powerPreview != null && powerList.Items.Cast<Fx.PowerList.Power>().Any(x => x.Prototype == st.Proto && x.Part == st.Part)
                && (powerPreview.HeroPowersReady || CharacterEntries().Count == 0)) break;
            await Task.Delay(100);
        }
        if (IsDisposed || powerPreview == null) return;
        await Task.Delay(300);   // (the list fills once more as the hero's powers come in)
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Prototype == st.Proto && x.Part == st.Part);
        if (at < 0) return;
        restoringPose = st.Pose;
        if (powerList.SelectedIndex != at) powerList.SelectedIndex = at;
        powerSideTabs?.Select(st.SideTab);
        if (st.Pose is not { } pose || st.Part != null) return;   // (a part tile plays its part on that pose: PlayPart)
        for (int i = 0; i < 100 && !IsDisposed && !powerPreview.AllAnimations.Any(a => a.Name.Equals(pose.Anim, StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(100);
        await Task.Delay(800);   // (the power's own pick plays first)
        if (!IsDisposed) powerPreview.HoldPose(pose.Anim, pose.Frame);
        restoringPose = null;
    }
    (string Anim, float Frame)? restoringPose;

    /// <summary>FillPowers is selecting the entry that was selected already (PowerSelected isn't run again).</summary>
    bool refillSame;
    readonly Label powerTitle = new() { Text = "" };
    /// <summary>The right column's Color and Parts tabs.</summary>
    FlatTabs? powerSideTabs;
    readonly Panel powerIcon = new() { BackColor = Color.Transparent };
    LightSlider? hueSlider, satSlider, brightSlider, opacitySlider;
    readonly Label powerCaption = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 4) };
    readonly Label powerShared = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 2, 0, 4), MaximumSize = new Size(560, 0) };
    Button? addParent;
    /// <summary>The NPCs' and enemies' own powers (Fx.AgentPowers), by package, once read; and each such power's package.</summary>
    readonly Dictionary<string, List<Fx.PowerList.Power>> agentPowers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Packages added with Add Package this session (the ones with a color are in draft.PowerColors as "pkg:").</summary>
    readonly List<string> addedPowerPackages = [];
    readonly Dictionary<string, string> agentOwner = new(StringComparer.OrdinalIgnoreCase);
    bool agentLoading;
    /// <summary>The package the selected own-effects entry's character extends (its model and most effects), when the mod
    /// doesn't hold it yet: Add It to This Mod brings it in.</summary>
    string? parentToAdd;
    readonly ColorSwatch[] presetSwatches = new ColorSwatch[5];
    int sharedRequest;
    bool fillingPower;
    Fx.GameData? powerDb;
    /// <summary>The bottom bar's Save Changes and Save and Keep Editing (their keys: Ctrl+Enter, Ctrl+S).</summary>
    Button? saveButton, saveStayButton;

    /// <summary>In the Powers and Animations tabs' 3D previews (whichever shows): Esc stops the animation (as on the Mods tab,
    /// Kurt: the Animations tab too), P plays / pauses it; not while typing. Otherwise the keys do what they did.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+S: Save and Keep Editing; Ctrl+Enter: Save Changes and back to the Mods tab (Kurt, 2026-10-23); as the buttons
        if (keyData == (Keys.Control | Keys.S) && saveStayButton is { Enabled: true, Visible: true }) { KeepEditing = true; SaveAsked(); return true; }
        if (keyData == (Keys.Control | Keys.Enter) && saveButton is { Enabled: true, Visible: true }) { KeepEditing = false; SaveAsked(); return true; }
        if (keyData is Keys.Escape or Keys.P && !Typing())
            foreach (var pv in new[] { powerPreview, animPreview })
                if (pv != null && pv.Visible && (keyData == Keys.Escape ? pv.PausePlayback() : pv.TogglePlayback())) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>The focus is in a text box (keys are typing).</summary>
    bool Typing()
    {
        Control? c = this;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c is TextBoxBase;
    }

    /// <summary>The packages the saved mod's power colors are built into (the preview reads the game's copies of those).</summary>
    HashSet<string> powerBuilt = [];

    Control PowersPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, (int)(6 * S), 0, 0) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        if (editing == null || game == null)
        {
            var note = new Label { Text = "Save the mod once, then come back here: power colors are previewed on its saved model.", AutoSize = true, Tag = "subtle", Padding = new Padding(8) };
            return Page(note, Toolbar(), "Recolor the hero's powers (every costume of that hero shows them).");
        }
        // The 3D preview is made when the tab first shows, after the editor is themed: the theme gives every control its
        // parent's (transparent) background, which the 3D view's surface can't take (it crashed the editor).
        var host = new Panel { Dock = DockStyle.Fill };
        host.VisibleChanged += (_, _) =>
        {
            if (!host.Visible || powerPreview != null || editing == null || game == null) return;
            powerPreview = new StorePreview { Dock = DockStyle.Fill, CookedFolder = game.Cooked, Catalog = catalog, ForceEffects = true, PowerStrip = false, Always3D = true };
            powerPreview.ColorFor = proto => draft.PowerColors.FirstOrDefault(e => e.Power.Equals(proto, StringComparison.OrdinalIgnoreCase)) is { } e ? e.Color : null;
            // Packages built for colors before are left out of the preview: the color shown is the one being set, on the stock files.
            powerBuilt = editing.Manifest.PowerColors?.SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            powerPreview.SkipModFile = f => powerBuilt.Contains(f);
            powerPreview.OverrideFiles = PreviewOverrides;   // borrowed looks and dodge ghosts built since the last save
            // effect parts switched off (Parts) aren't shown, saved or not (Kurt, 2026-10-18: a recolored power's preview reads
            // the game's package, where they're still on)
            powerPreview.HiddenEffect = name => draft.PartsOff.Any(x => x.Switch == "effect" && x.Part[(x.Part.LastIndexOf('.') + 1)..].Equals(name, StringComparison.OrdinalIgnoreCase));
            powerPreview.HeroPowersLoaded += FillPowers;
            // Its buttons (made when a model loads) take the app's button look; the theme pass that gives it them in the
            // main window has run already here.
            powerPreview.ControlAdded += (_, _) => Ui.RestyleButtons(powerPreview);
            host.Controls.Add(powerPreview);
            powerPreview.Mod = editing;
            BeginInvoke(FillPowers);   // a mod without a hero package (NPCs, enemies only) lists its own effects at once
        };
        // Layout (Kurt, 2026-10-17: a UI pass; the 3D view was much smaller here than on the other tabs): the view fills the
        // left column with the powers as icon tiles under it (PowerBar, as the Mods and Model tabs' power buttons); the right
        // column is the selected power: its name, its color (sliders, quick actions, presets, its colors), then its Parts.
        var left = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        powerList.Dock = DockStyle.Bottom;
        powerList.Padding = new Padding(0, (int)(6 * S), 0, 0);
        powerList.DrawTile = DrawPowerTile;
        powerList.TipFor = o => o is Fx.PowerList.Power p ? PowerTip(p) : "";
        powerList.StartsGroup = i => i == heroPowerCount && i > 0;
        powerList.SelectedIndexChanged += (_, _) => { ShowPowerTitle(); if (!refillSame) PowerSelected(); };
        left.Controls.Add(host);
        left.Controls.Add(powerList);
        page.Controls.Add(left, 0, 0);

        // right: the selected entry's name over two tabs, Color (sliders, presets, its colors) and Parts (its effects, sounds,
        // props and settings to switch off), so each has the column's full height
        var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding((int)(12 * S), 0, 0, 0), Margin = new Padding(0) };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        side.RowStyles.Add(new RowStyle(SizeType.AutoSize)); side.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var colorScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var right = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 9, Padding = new Padding(0, (int)(4 * S), (int)(8 * S), 0), Margin = new Padding(0) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int k = 0; k < 9; k++) right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        colorScroll.Controls.Add(right);
        // the selected power: its icon, name and what it is
        var title = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, (int)(4 * S)), BackColor = Color.Transparent };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        powerIcon.Size = new Size((int)(44 * S), (int)(44 * S)); powerIcon.Margin = new Padding(0, 0, (int)(10 * S), 0);
        powerIcon.Paint += (_, e) => { if (powerList.SelectedItem is Fx.PowerList.Power p) DrawPowerTile(e.Graphics, new Rectangle(0, 0, powerIcon.Width - 1, powerIcon.Height - 1), p, false, false); };
        title.Controls.Add(powerIcon, 0, 0); title.SetRowSpan(powerIcon, 2);
        powerTitle.Font = Ui.Bold(12f); powerTitle.AutoSize = true; powerTitle.Margin = new Padding(0, 0, 0, 0);
        powerCaption.Padding = new Padding(0, 2, 0, 0);
        title.Controls.Add(powerTitle, 1, 0); title.Controls.Add(powerCaption, 1, 1);
        side.Controls.Add(title, 0, 0);
        var hint = new Label { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, (int)(6 * S)),
            Text = "A color changes the power's own game files, so every costume of the hero shows it. Save writes them into the mod's packages." };
        Ui.Tip(hint, "A color recolors the power's own game files (its particles, effect textures and material colors): every costume of the hero shows it, for anyone using this mod. Save writes them into the mod's packages.");
        right.Controls.Add(hint, 0, 1);
        hueSlider = new LightSlider { Label = "Hue", Min = -180, Max = 180, Step = 1, Mark = 0, Format = v => $"{v:+0;-0;0}°", Home = () => 0, Height = (int)(28 * S) };
        satSlider = new LightSlider { Label = "Saturation", Min = 0, Max = 2, Step = 0.01f, Mark = 1, Format = v => $"{v * 100:0} %", Home = () => 1, Height = (int)(28 * S) };
        brightSlider = new LightSlider { Label = "Brightness", Min = 0, Max = 3, Step = 0.01f, Mark = 1, Format = v => $"{v * 100:0} %", Home = () => 1, Height = (int)(28 * S) };
        opacitySlider = new LightSlider { Label = "Opacity", Min = 0, Max = 10, Step = 0.01f, Mark = 1, Format = v => $"{v * 100:0} %", Home = () => 1, Height = (int)(28 * S) };
        var sliders = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0), BackColor = Color.Transparent };
        sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var sl in new[] { hueSlider, satSlider, brightSlider, opacitySlider }) { sl.ValueChanged += SliderChanged; sl.Dock = DockStyle.Fill; sl.Margin = new Padding(0); sliders.Controls.Add(sl); }
        right.Controls.Add(sliders, 0, 3);
        Ui.Tip(opacitySlider, "How visible the power's effects are: 100 % as the game has it, lower fades them, 0 % switches them off (nothing draws). Above 100 % they're more solid (their particles' and effect textures' alpha multiplied), for dark colors. Unlike Brightness, it fades smoke and dust too instead of leaving them as a dark cloud. Double-click: back to 100 %.\n\nFor dark powers: Dark (Saturation 0 %, Brightness 0 %, Opacity 500 %; tested in game on Nova's Magneton Pulsar). Impact: Additive glows, beams and sparks are redrawn with a translucent game material so they darken too; mesh and distortion parts can still fade. Check it in game.");
        Ui.Tip(hueSlider, "Turns the power's colors around the color wheel (the brightness of each color is kept). Double-click: back to the game's.");
        Ui.Tip(satSlider, "How strong the power's colors are: 0 % is grey, 100 % as the game has it. Double-click: back to 100 %.");
        Ui.Tip(brightSlider, "How bright the power's colors are. Double-click: back to 100 %.");
        var reset = Ui.FlatButton("Game's Colors", () => SetColor(null), tip: "Take this power back to the game's own colors.");
        var all = Ui.FlatButton("Apply to All Powers", ApplyToAll, tip: "Give every power of the hero the color on the sliders (the game's colors take them all back).");
        var dark = Ui.FlatButton("Dark", SetDark, tip: "Makes this power dark: Saturation 0 %, Brightness 0 %, Opacity 500 % (tested in game: dark, fairly solid). Impact: Translucent effects (fields, smoke) turn dark as they are; additive glows, beams and sparks are redrawn with a copy of a translucent game material on Save, so they darken too. Mesh and distortion parts can still fade, and the 3D preview doesn't show the redrawn ones. Check it in game; the sliders fine-tune it.");
        var addPkg = Ui.FlatButton("Add Package", AddPowerPackage, tip: "Adds one of the game's effect packages (UC__Power…, a projectile, a hotspot, a condition effect) to the list under its file name, for a power that isn't listed or isn't fully recolored. Impact: Everything in that package is recolored, for every hero, team-up or enemy that uses it, and the 3D preview doesn't show its color (check it in game).");
        // quick actions and the five presets on one row (as the MHO Hero Creator's presets: click = use on this power,
        // right-click = store the sliders there)
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Padding = new Padding(0, (int)(4 * S), 0, 0), Margin = new Padding(0), BackColor = Color.Transparent };
        actions.Controls.AddRange([reset, dark, all, addPkg]);
        var presetLabel = new Label { Text = "Presets", AutoSize = true, Tag = "subtle", Margin = new Padding((int)(10 * S), (int)(9 * S), (int)(6 * S), 0) };
        Ui.Tip(presetLabel, "Five colors kept on this PC: click one to use it on this power, right-click to store the sliders there.");
        actions.Controls.Add(presetLabel);
        for (int i = 0; i < 5; i++)
        {
            int k = i;
            var sw = new ColorSwatch { Size = new Size((int)(28 * S), (int)(26 * S)), Margin = new Padding(0, (int)(5 * S), (int)(4 * S), 0), Number = k + 1 };
            sw.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) StorePreset(k); else if (e.Button == MouseButtons.Left) UsePreset(k); };
            presetSwatches[k] = sw;
            actions.Controls.Add(sw);
        }
        ShowPresets();
        right.Controls.Add(actions, 0, 4);
        right.Controls.Add(PaletteSection(), 0, 5);
        if (WhatsNew.PowerBorrow) right.Controls.Add(BorrowSection(), 0, 6);   // tabled, preview only (WhatsNew.PowerBorrowReleased)
        addParent = Ui.FlatButton("Add It to This Mod", AddParentPackage, tip: "Adds the package this character's class extends (its model, and most of its effects such as glows and trails) to the mod, as the game's own copy, and selects its Own Effects so you can recolor them.");
        addParent.Visible = false;
        var sharedBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0), BackColor = Color.Transparent };
        sharedBox.Controls.Add(powerShared); sharedBox.Controls.Add(addParent);
        right.Controls.Add(sharedBox, 0, 7);
        powerSideTabs = new FlatTabs { Dock = DockStyle.Fill, TabPoints = 9f };
        powerSideTabs.Add("Color", colorScroll);
        powerSideTabs.Add("Parts", PartsSection());
        side.Controls.Add(powerSideTabs, 0, 1);
        // wrapped notes follow the column's width
        side.SizeChanged += (_, _) =>
        {
            var max = new Size(Math.Max(100, side.ClientSize.Width - side.Padding.Horizontal - (int)(30 * S)), 0);
            foreach (var l in new[] { hint, powerShared, partsNote, paletteNote }) l.MaximumSize = max;
        };
        page.Controls.Add(side, 1, 0);
        Disposed += (_, _) => powerPreview?.Dispose();
        return page;
    }

    void FillPowers()
    {
        if (powerPreview == null) return;
        string? keep = (powerList.SelectedItem as Fx.PowerList.Power)?.Prototype;
        string? keepPart = (powerList.SelectedItem as Fx.PowerList.Power)?.Part;
        var keepAnims = (powerList.SelectedItem as Fx.PowerList.Power)?.Animations.ToList();
        powerList.BeginUpdate();
        powerList.Items.Clear();
        foreach (var p in powerPreview.AllHeroPowers) powerList.Items.Add(p);
        heroPowerCount = powerList.Items.Count;
        foreach (var p in OwnEffects())
        {
            powerList.Items.Add(p);
            // then that character's own powers (read in the background, once)
            string file = p.Prototype[PowerColorBuild.OwnPrefix.Length..];
            if (agentPowers.TryGetValue(file, out var list)) foreach (var ap in list) powerList.Items.Add(ap);
        }
        // each hero's own parts (hover, dodge …: Invisible Woman's Can Transition to Hover; the Parts list only)
        foreach (var c in CharacterEntries())
        {
            powerList.Items.Add(c);
            // then its own effect parts as tiles of their own (Kurt, 2026-10-18: the dodge took Char → Parts → ▶): Dodge, Level
            // Up, Death …; a click shows the Character entry's parts and plays that one
            foreach (var pe in CharacterPartEntries(c)) powerList.Items.Add(pe);
        }
        // packages added by hand (Add Package), under their file names
        foreach (string f in draft.PowerColors.Where(e => PowerColorBuild.IsPackage(e.Power)).Select(e => e.Power[PowerColorBuild.PackagePrefix.Length..])
                     .Concat(addedPowerPackages).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            powerList.Items.Add(new Fx.PowerList.Power(PowerColorBuild.PackagePrefix + f, f, null, []) { HasName = true });
        LoadAgentPowers();
        powerList.EndUpdate();
        var shown = powerList.Items.Cast<Fx.PowerList.Power>().ToList();
        int at = keep == null ? -1 : shown.FindIndex(p => p.Prototype == keep && p.Part == keepPart);
        // the manual's screenshots (MHO_EXTMM_SNAP_POWER: part of a power's name, e.g. "Own Effects"): that entry first
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_SNAP_POWER") is { Length: > 0 } snapPower && shown.FindIndex(p => p.Name.Contains(snapPower, StringComparison.OrdinalIgnoreCase)) is int snapAt and >= 0)
            at = snapAt;   // (also on the second fill: the agent powers arrive later)
        // (not before the hero's powers are in: the first entry would be a character's own effects, whose model the preview
        // then shows, and the hero's powers never load for it)
        bool waiting = !powerPreview.HeroPowersReady && CharacterEntries().Count > 0 && at < 0;
        // the same entry again (the list filled again after the preview reloaded: a ghost build, the hero's powers coming in):
        // selected without starting it over, so the animation and frame showing stay (Kurt, 2026-10-17)
        // (but not when its animations changed: a borrowed look gives Shield Toss Thor's throw, which then plays; Kurt,
        // 2026-10-23: it showed only after Save, closing and opening again)
        refillSame = keep != null && at >= 0 && at < shown.Count && shown[at].Prototype == keep && shown[at].Part == keepPart
            && keepAnims != null && shown[at].Animations.SequenceEqual(keepAnims, StringComparer.OrdinalIgnoreCase);
        try { if (powerList.Items.Count > 0 && !waiting) powerList.SelectedIndex = Math.Max(0, at); }
        finally { refillSame = false; }
        if (powerList.Items.Count == 0) { powerCaption.Text = "No Powers Found for This Hero"; ShowPowerTitle(); }
    }

    /// <summary>The mod's NPC / enemy / team-up packages' own powers, read in the background (the game data and a scan of
    /// its characters, about a second the first time); the list fills again when they're in.</summary>
    void LoadAgentPowers()
    {
        if (agentLoading || game == null) return;
        var files = draft.Packages.Select(x => x.File).Where(f => PowerColorBuild.HasOwnEffects(f) && !agentPowers.ContainsKey(f)
            && !f.StartsWith("UC__MarvelPlayerAudio_", StringComparison.OrdinalIgnoreCase)).ToList();   // (an audio package is no character with an AI's powers)
        if (files.Count == 0) return;
        agentLoading = true;
        var g = game;
        Task.Run(() =>
        {
            powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
            var got = new Dictionary<string, List<Fx.PowerList.Power>>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string label = OwnEffects().FirstOrDefault(o => o.Prototype.EndsWith(f, StringComparison.OrdinalIgnoreCase))?.Name.Replace(": Own Effects", "") ?? PowerColorBuild.ClassOf(f);
                try { got[f] = Fx.AgentPowers.List(powerDb, PowerColorBuild.ClassOf(f), g.Cooked, label); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { got[f] = []; }
            }
            return got;
        }).ContinueWith(t =>
        {
            agentLoading = false;
            if (IsDisposed || t.Status != TaskStatus.RanToCompletion) return;
            foreach (var (f, list) in t.Result)
            {
                agentPowers[f] = list;
                foreach (var p in list) agentOwner[p.Prototype] = f;
            }
            if (t.Result.Values.Any(l => l.Count > 0)) FillPowers();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>An entry per NPC or enemy package of the mod: its own effects (Kurt, 2026-10-05: the Sinister clones' red glow,
    /// in their attachment's particle systems), recolored like a power.</summary>
    List<Fx.PowerList.Power> OwnEffects()
    {
        var list = new List<Fx.PowerList.Power>();
        var homes = new List<string>();
        foreach (var (pf, src) in draft.Packages.Where(p => PowerColorBuild.HasOwnEffects(p.File)).OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase))
        {
            // a costume with no always-on effects of its own (Ghost Rider ModernVU): its hero's entry instead, even before that
            // package is in the mod (Save adds it; Kurt, 2026-10-12: the costume's own entry could never keep a color)
            string? home = PowerColorBuild.IsHero(pf) ? PowerColorBuild.OwnHome(pf, src) : pf;
            if (home != null && !homes.Contains(home, StringComparer.OrdinalIgnoreCase)) homes.Add(home);
        }
        foreach (string file in homes)
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            string name = stem[(stem.IndexOf('_', 4) + 1)..];
            if (name.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
            name = System.Text.RegularExpressions.Regex.Replace(name.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])", " ");
            list.Add(new Fx.PowerList.Power(PowerColorBuild.OwnPrefix + file, $"{name}: Own Effects", null, []) { HasName = true });
        }
        return list;
    }

    /// <summary>
    /// Add Package (a user, 2026-10-13: Iron Man's plain Unibeam package wasn't in the list): one of the game's effect packages
    /// added to the Powers list under its file name; a color recolors the whole package, like a power's packages.
    /// </summary>
    void AddPowerPackage()
    {
        if (game == null) return;
        using var d = new OpenFileDialog { Title = "Add a Power Package", InitialDirectory = game.Cooked, RestoreDirectory = true,
            Filter = "Power packages (UC__Power*.upk)|UC__Power*.upk|All effect packages (UC__*.upk)|UC__*.upk" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string f = Path.GetFileName(d.FileName);
        string? real = Directory.EnumerateFiles(game.Cooked, f).Select(Path.GetFileName).FirstOrDefault();
        if (real == null || !Path.GetFullPath(Path.GetDirectoryName(d.FileName)!).TrimEnd('\\').Equals(Path.GetFullPath(game.Cooked).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Dialog.Show(this, "Pick a package in the game's CookedPCConsole folder: The mod replaces the game's file of that name.", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (PowerColorBuild.IsCharacter(real))
        {
            Dialog.Show(this, $"{real} is a character's package. Add it on the Packages tab instead: Its glows and trails then show as an Own Effects entry here, which keeps the character's model as it is.", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string key = PowerColorBuild.PackagePrefix + real;
        if (!addedPowerPackages.Contains(real, StringComparer.OrdinalIgnoreCase)) addedPowerPackages.Add(real);
        FillPowers();
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Prototype.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) powerList.SelectedIndex = at;
    }

    /// <summary>Who else the selected power's recolor changes (its packages' other owners: a team-up's copy, Rogue …),
    /// worked out in the background.</summary>
    void ShowShared(Fx.PowerList.Power p)
    {
        if (game == null) return;
        int req = ++sharedRequest;
        if (addParent != null) addParent.Visible = false;
        parentToAdd = null;
        if (PowerColorBuild.IsPackage(p.Prototype))
        {
            string pf = p.Prototype[PowerColorBuild.PackagePrefix.Length..];
            powerShared.Text = $"Added by hand: Its color goes into all of {pf} (every particle, effect texture and material color in it), and Save puts that package in the mod. "
                + "Impact: Every hero, team-up or enemy power that uses this package changes too, and the 3D preview doesn't show this color, so check it in game. "
                + "To take it out: The Game's Colors, then Save.";
            return;
        }
        if (PowerColorBuild.IsOwn(p.Prototype))
        {
            string f = p.Prototype[PowerColorBuild.OwnPrefix.Length..];
            string text = $"Its color goes into {f} itself; a model built into it stays.";
            if (!draft.Packages.Any(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase)))
                text = $"These effects are in {f} (this mod's costume takes them from it): Save adds that package to the mod (the game's copy) with their color. It then reaches the hero's costumes that use them.";
            powerShared.Text = text;
            // a character that extends another package's (the Sinister Medal pets: the mob clones' model and red glow)
            string? path = draft.Packages.FirstOrDefault(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase)).Source;
            var gc = game;
            if (path == null || gc == null) return;
            Task.Run(() => PowerColorBuild.ParentPackage(path, gc.Cooked)).ContinueWith(t =>
            {
                if (IsDisposed || req != sharedRequest || t.Status != TaskStatus.RanToCompletion || t.Result is not string parent) return;
                bool held = draft.Packages.Any(x => x.File.Equals(parent, StringComparison.OrdinalIgnoreCase));
                powerShared.Text = held ? $"Its model and most effects (glows, trails) come from {parent}: recolor them in its Own Effects."
                    : $"Its model and most effects (glows, trails) come from {parent}, which this mod doesn't hold:";
                if (!held && addParent != null) { parentToAdd = parent; addParent.Visible = true; }
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        if (agentOwner.TryGetValue(p.Prototype, out var aowner))
        {
            powerShared.Text = "";
            var ga = game;
            Task.Run(() =>
            {
                powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(ga.Root, "Data", "Game", "Calligraphy.sip")));
                string cls = PowerColorBuild.ClassOf(aowner);
                return (PowerRecolor.PackagesOfAgent(powerDb, p.Prototype, cls, ga.Cooked).Count, PowerRecolor.SharedWithAgent(powerDb, p.Prototype, cls, ga.Cooked));
            }).ContinueWith(t =>
            {
                if (IsDisposed || req != sharedRequest || t.Status != TaskStatus.RanToCompletion) return;
                var (n, shared) = t.Result;
                powerShared.Text = $"Its color goes into {n} package(s)." + (shared.Count > 0 ? "\nAlso changes, as they use the same effects: " + string.Join(", ", shared) + "." : "");
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        string? hero = draft.Packages.Select(x => HeroOf.Package(x.File, game?.Cooked)).FirstOrDefault(h => h != null);
        if (hero == null) { powerShared.Text = ""; return; }
        powerShared.Text = "";
        var g = game;
        Task.Run(() =>
        {
            powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
            int n = PowerRecolor.PackagesOf(powerDb, p.Prototype, hero, g.Cooked).Count;
            return (n, PowerRecolor.SharedWith(powerDb, p.Prototype, hero, g.Cooked));
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != sharedRequest || t.Status != TaskStatus.RanToCompletion) return;
            var (n, shared) = t.Result;
            powerShared.Text = $"Its color goes into {n} package(s) (its own and what it sets off: missiles, hotspots, conditions, summons)."
                + (shared.Count > 0 ? "\nAlso changes, as they use the same effects: " + string.Join(", ", shared) + "." : "");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Before saving (Kurt, 2026-10-12): an Own Effects color set on a costume whose always-on effects are its hero's (Ghost
    /// Rider ModernVU: his flaming skull is in the base package) would do nothing, as the costume has none to recolor. Asks:
    /// add the hero's package and move the color to its Own Effects (Add It and Save), save without it, or cancel. False:
    /// cancelled.
    /// </summary>
    bool OwnColorsAtHome()
    {
        if (game == null) return true;
        // a color on a hero's entry whose package isn't in the mod yet (the costume's effects are its hero's): the package is
        // added (the game's clean copy)
        var added = new List<string>();
        foreach (var e in draft.PowerColors.Where(e => PowerColorBuild.IsOwn(e.Power) && !e.Color.IsNone).ToList())
        {
            string f = e.Power[PowerColorBuild.OwnPrefix.Length..];
            if (draft.Packages.Any(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
            if (!AddPackageFromGame(f)) return false;
            added.Add(f);
        }
        if (added.Count > 0) { RefreshPackages(); powerCaption.Text = Ui.TitleCase($"Added {string.Join(", ", added)} for its own effects' color"); }
        var moves = new List<(PowerColorEntry Entry, string File, string Parent)>();
        foreach (var e in draft.PowerColors.Where(e => PowerColorBuild.IsOwn(e.Power) && !e.Color.IsNone).ToList())
        {
            string f = e.Power[PowerColorBuild.OwnPrefix.Length..];
            if (!PowerColorBuild.IsHero(f)) continue;
            string? src = draft.Packages.FirstOrDefault(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase)).Source;
            if (src == null || !File.Exists(src) || PowerColorBuild.OwnSystemsIn(src) > 0) continue;
            if (PowerColorBuild.ParentPackage(src, game.Cooked) is string parent) moves.Add((e, f, parent));
        }
        if (moves.Count == 0) return true;
        string list = string.Join("\n", moves.Select(m => $"{m.Entry.Name}: its effects are in {m.Parent}"));
        int pick = Dialog.Choose(this, $"These colors won't be saved as they are: The costume has no always-on effects of its own, its hero's package has them, and this mod doesn't hold it.\n\n{list}\n\n  • Add It and Save: Adds that package (the game's copy) and puts the color on its Own Effects. It then reaches the hero's costumes that use those effects.\n  • Save Without It: The color is dropped.",
            "Own Effects Not in This Mod", "Add It and Save", "Save Without It", "Cancel");
        if (pick == 2 || pick < 0) return false;
        foreach (var (e, f, parent) in moves)
        {
            if (pick == 1) { draft.PowerColors.Remove(e); continue; }
            if (!AddPackageFromGame(parent)) return false;
            string power = PowerColorBuild.OwnPrefix + parent;
            draft.PowerColors.RemoveAll(x => x != e && x.Power.Equals(power, StringComparison.OrdinalIgnoreCase));   // (one color for that package: this one)
            e.Power = power;
            e.Name = OwnEffects().FirstOrDefault(o => o.Prototype.Equals(power, StringComparison.OrdinalIgnoreCase))?.Name ?? e.Name;
        }
        RefreshPackages();
        FillPowers();
        // the entry that holds the color now, selected (Kurt, 2026-10-12: the costume's entry stayed selected, showing the game's
        // colors, and the values looked lost)
        if (pick == 0 && moves.Count > 0)
        {
            string moved = moves[0].Entry.Power;
            int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Prototype.Equals(moved, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) powerList.SelectedIndex = at;
            powerCaption.Text = Ui.TitleCase($"Color moved to {moves[0].Entry.Name}");
        }
        return true;
    }

    /// <summary>A game package (the clean original) into the mod's packages; false (with a message) when there's no clean copy.</summary>
    bool AddPackageFromGame(string f)
    {
        if (game == null) return false;
        if (draft.Packages.Any(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase))) return true;
        string? source = new Originals(lib.DataFolder, game).Find(f) ?? (StockFiles.For(game.Cooked, f) is var st && File.Exists(st) ? st : null);
        if (source == null) { Dialog.Show(this, $"{f}: No clean original of this package (the game's copy is changed).", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        draft.Packages.Add((f, source));
        return true;
    }

    /// <summary>Add It to This Mod: the parent package (the game's original copy) into the mod's packages, then its Own Effects
    /// entry selected.</summary>
    void AddParentPackage()
    {
        if (parentToAdd is not string f || game == null) return;
        string? source = new Originals(lib.DataFolder, game).Find(f) ?? (StockFiles.For(game.Cooked, f) is var st && File.Exists(st) ? st : null);
        if (source == null) { Dialog.Show(this, $"{f}: No clean original of this package (the game's copy is changed).", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!draft.Packages.Any(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase))) draft.Packages.Add((f, source));
        RefreshPackages();
        if (addParent != null) addParent.Visible = false;
        parentToAdd = null;
        FillPowers();
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Prototype.Equals(PowerColorBuild.OwnPrefix + f, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) powerList.SelectedIndex = at;
        powerCaption.Text = Ui.TitleCase($"Added {f}: Save Changes keeps it");
    }

    /// <summary>The sliders' color on every power of the hero (Kurt: one color shift for the whole hero).</summary>
    void ApplyToAll()
    {
        if (powerPreview == null || hueSlider == null || satSlider == null || brightSlider == null) return;
        var c = new PowerColor(hueSlider.Value, satSlider.Value, brightSlider.Value) { Opacity = opacitySlider?.Value ?? 1 };
        foreach (var p in powerPreview.AllHeroPowers)
        {
            var e = EntryOf(p.Prototype);
            if (e == null) { if (c.IsNone) continue; draft.PowerColors.Add(e = new PowerColorEntry { Power = p.Prototype, Name = p.Name }); }
            e.Hue = c.Hue; e.Saturation = c.Saturation; e.Brightness = c.Brightness; e.Opacity = c.Opacity;
        }
        powerCaption.Text = Ui.TitleCase(c.IsNone ? $"All {powerPreview.AllHeroPowers.Count} powers back to the game's colors" : $"Color applied to all {powerPreview.AllHeroPowers.Count} powers");
        powerList.Invalidate();
        powerPreview.RefreshPowerColor();
    }

    void StorePreset(int k)
    {
        if (hueSlider == null || satSlider == null || brightSlider == null) return;
        PreviewViews.SetPowerPreset(k, new PowerColor(hueSlider.Value, satSlider.Value, brightSlider.Value));
        ShowPresets();
        powerCaption.Text = Ui.TitleCase($"Preset {k + 1} stored");
    }

    void UsePreset(int k)
    {
        if (PreviewViews.PowerPreset(k) is not { } c) { powerCaption.Text = Ui.TitleCase($"Preset {k + 1} is empty: right-click it to store the sliders"); return; }
        if (powerList.SelectedItem is not Fx.PowerList.Power || hueSlider == null || satSlider == null || brightSlider == null) return;
        fillingPower = true; hueSlider.Value = c.Hue; satSlider.Value = c.Saturation; brightSlider.Value = c.Brightness; fillingPower = false;
        SetColor(c with { Opacity = opacitySlider?.Value ?? 1 });   // (presets are colors; the opacity stays)
    }

    void ShowPresets()
    {
        for (int k = 0; k < 5; k++)
        {
            var c = PreviewViews.PowerPreset(k);
            var sw = presetSwatches[k];
            if (sw == null) continue;
            sw.Swatch = c == null ? null : SwatchColor(c);
            Ui.Tip(sw, c == null ? $"Preset {k + 1} (empty). Right-click: store the sliders here (kept on this PC for every mod)."
                : $"Preset {k + 1}: hue {c.Hue:0}°, saturation {c.Saturation * 100:0} %, brightness {c.Brightness * 100:0} %. Click: use it on this power. Right-click: store the sliders here instead.");
        }
    }

    /// <summary>A lightning blue turned by the color, as a hint of what it does (the swatches and the list's dots).</summary>
    static Color SwatchColor(PowerColor c)
    {
        var v = c.Apply(new System.Numerics.Vector3(0.25f, 0.35f, 1f));
        float m = Math.Max(1f, Math.Max(v.X, Math.Max(v.Y, v.Z)));
        return Color.FromArgb(Math.Clamp((int)(v.X / m * 255), 0, 255), Math.Clamp((int)(v.Y / m * 255), 0, 255), Math.Clamp((int)(v.Z / m * 255), 0, 255));
    }

    PowerColorEntry? EntryOf(string proto) => draft.PowerColors.FirstOrDefault(e => e.Power.Equals(proto, StringComparison.OrdinalIgnoreCase));

    void PowerSelected()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p || hueSlider == null || satSlider == null || brightSlider == null) return;
        if (!IsCharacterEntry(p.Prototype)) HideGhosts();
        fillingPower = true;
        var e = EntryOf(p.Prototype);
        hueSlider.Value = e?.Hue ?? 0; satSlider.Value = e?.Saturation ?? 1; brightSlider.Value = e?.Brightness ?? 1;
        if (opacitySlider != null) opacitySlider.Value = e?.Opacity ?? 1;
        fillingPower = false;
        // a character's own parts (the dodge, a hover): their effects take the color too (Kurt, 2026-10-17)
        if (IsCharacterEntry(p.Prototype)) { CharacterSelected(p); powerPreview?.FrameFull(); return; }
        powerCaption.Text = (e == null ? "The Game's Colors" : "Recolored")
            // (no animation of its own: The Will of Doom, procs, passives: the preview plays its effects over the idle, as the
            // Mods tab and the Model tab do; Kurt, 2026-10-12)
            + (p.Animations.Count == 0 ? " · No Animation of Its Own: Its Effects Play Over the Idle" : "");
        if (agentOwner.TryGetValue(p.Prototype, out var owner))
        {
            if (powerPreview != null) powerPreview.OverridesOff = false;
            powerPreview?.PlayAgentPower(owner, p.Prototype, p.Animations);
        }
        else if (PowerColorBuild.IsPackage(p.Prototype))
        {
            powerCaption.Text = (e == null ? "The Game's Colors" : "Recolored") + " · A Whole Package (Not Shown in the Preview)";
            if (powerPreview != null) powerPreview.OverridesOff = false;
        }
        else if (PowerColorBuild.IsOwn(p.Prototype))
        {
            powerCaption.Text = (e == null ? "The Game's Colors" : "Recolored") + " · This Character's Own Effects (Glows, Trails …)";
            if (powerPreview != null) powerPreview.OverridesOff = (e?.Color.Opacity ?? 1) < PowerColor.Invisible;
            powerPreview?.PlayOwn(p.Prototype[PowerColorBuild.OwnPrefix.Length..], p.Prototype);
        }
        else
        {
            if (powerPreview != null) powerPreview.OverridesOff = false;
            powerPreview?.PlayPower(p.Prototype);
        }
        ShowShared(p);
        ShowParts(p);
        ShowBorrow(p);
        LoadPalette(p);
        ShowMapRows();
        powerPreview?.FrameFull();   // (the whole character, as the power buttons do)
    }

    /// <summary>The Dark button: the selected power dark and more solid (Saturation 0, Brightness 0, Opacity 500 %: the value
    /// that looked right in game on Nova's Magneton Pulsar, 2026-10-15).</summary>
    void SetDark()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power || satSlider == null || brightSlider == null || opacitySlider == null) return;
        fillingPower = true;
        satSlider.Value = 0; brightSlider.Value = 0; opacitySlider.Value = 5;
        fillingPower = false;
        SliderChanged();
    }

    void SliderChanged()
    {
        if (fillingPower || hueSlider == null || satSlider == null || brightSlider == null) return;
        SetColor(new PowerColor(hueSlider.Value, satSlider.Value, brightSlider.Value) { Opacity = opacitySlider?.Value ?? 1 });
    }

    /// <summary>The selected power's color (null or no change: the game's); the preview shows it at once.</summary>
    void SetColor(PowerColor? c)
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p) return;
        var e = EntryOf(p.Prototype);
        if (c == null || c.IsNone)
        {
            if (e != null) { e.Hue = 0; e.Saturation = 1; e.Brightness = 1; e.Opacity = 1; if (c == null) e.Maps = null; }   // kept until Save, so its old packages are dropped then
            if (c == null && hueSlider != null && satSlider != null && brightSlider != null) { fillingPower = true; hueSlider.Value = 0; satSlider.Value = 1; brightSlider.Value = 1; if (opacitySlider != null) opacitySlider.Value = 1; fillingPower = false; }
        }
        else
        {
            if (e == null) draft.PowerColors.Add(e = new PowerColorEntry { Power = p.Prototype, Name = p.Name, Owner = agentOwner.GetValueOrDefault(p.Prototype) });
            e.Hue = c.Hue; e.Saturation = c.Saturation; e.Brightness = c.Brightness; e.Opacity = c.Opacity;
        }
        powerCaption.Text = (EntryOf(p.Prototype) is { } now && !now.Color.IsNone ? "Recolored" : "The Game's Colors") + (IsCharacterEntry(p.Prototype) ? " · This Character's Own Parts (Dodge, Hover …)" : "");
        powerList.Invalidate();
        powerPreview?.RefreshPowerColor();
        // a hologram on the character's model comes off at Opacity 0 (as Save writes it)
        if (PowerColorBuild.IsOwn(p.Prototype) && powerPreview != null) powerPreview.OverridesOff = (EntryOf(p.Prototype)?.Color.Opacity ?? 1) < PowerColor.Invisible;
        if (c == null) ShowMapRows();
    }

    /// <summary>A power's tile in the bar (and the big one beside its name): its icon, else a short word for what it is; a dot
    /// of its color change in the corner, a mark when it borrows another power's look.</summary>
    void DrawPowerTile(Graphics g, Rectangle r, object item, bool sel, bool hot)
    {
        if (item is not Fx.PowerList.Power p) return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int rad = Math.Max(2, (int)(5 * S));
        using (var path = Rounded(r, rad))
        {
            using (var b = new SolidBrush(sel ? Ui.CardHover : hot ? Color.FromArgb(52, 52, 60) : Ui.Card)) g.FillPath(b, path);
            if (p.Icon != null && game != null && Fx.PowerList.Icon(p.Icon, game.Cooked) is { } img)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var clip = g.Clip; g.SetClip(path);
                g.DrawImage(img, Rectangle.Inflate(r, -(int)(2 * S), -(int)(2 * S)));
                g.Clip = clip;
            }
            else
            {
                string word = p.Part != null ? PartWord(p.Name) : PowerColorBuild.IsOwn(p.Prototype) ? "FX" : IsCharacterEntry(p.Prototype) ? "Char" : PowerColorBuild.IsPackage(p.Prototype) ? "UPK"
                    : string.Concat(p.Name.Split(' ', ':').Where(w => w.Length > 0 && char.IsLetter(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));
                if (word.Contains('\n'))
                {
                    // (two lines: a part tile's first and last word)
                    using var f = Ui.Heavy(r.Height > 40 * S ? 9.5f : 7f);
                    using var br = new SolidBrush(Ui.Subtle);
                    using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None };
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    g.DrawString(word, f, br, r, sf);
                }
                else
                {
                    // (a longer word smaller, so it fits the tile: Dodge)
                    float pt = (r.Height > 40 * S ? 11f : 8.5f) * Math.Min(1f, 4f / Math.Max(1, word.Length));
                    using var f1 = Ui.Heavy(Math.Max(6f, pt));
                    TextRenderer.DrawText(g, word, f1, r, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
            using var pen = new Pen(sel ? Ui.Accent : hot ? Ui.Subtle : Color.FromArgb(60, 60, 68), sel ? Math.Max(2, 2 * S) : 1);
            g.DrawPath(pen, path);
        }
        int d = Math.Max(6, (int)(r.Width * 0.26f));
        var entry = EntryOf(p.Prototype);
        if (entry != null && !entry.Color.IsNone)
        {
            // (a power with replaced colors shows its first replacement's new color)
            using var sw = new SolidBrush(entry.Maps is { Count: > 0 } ms && ColorMap.FromHex(ms[0].To) is { } tc ? ToColor(tc) : SwatchColor(entry.Color));
            var dot = new Rectangle(r.Right - d - 1, r.Top + 1, d, d);
            g.FillEllipse(sw, dot);
            using var edge = new Pen(Color.FromArgb(20, 20, 24), 1);
            g.DrawEllipse(edge, dot);
        }
        if (BorrowOf(p.Prototype) != null)
        {
            using var b = new SolidBrush(Ui.Accent);
            g.FillPolygon(b, new Point[] { new Point(r.Left + 1, r.Bottom - d), new Point(r.Left + 1, r.Bottom - 1), new Point(r.Left + d, r.Bottom - 1) });
        }
    }

    static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int rad)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = rad * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A tile's tooltip: the name, whose look it borrows, its color.</summary>
    string PowerTip(Fx.PowerList.Power p)
    {
        var lines = new List<string> { p.Name };
        if (BorrowOf(p.Prototype) is { } borrow) lines.Add("Looks like " + borrow.DonorName);
        if (EntryOf(p.Prototype) is { } e && !e.Color.IsNone) lines.Add("Recolored");
        if (PowerColorBuild.IsOwn(p.Prototype)) lines.Add("The character's own effects (glows, trails)");
        else if (p.Part != null) lines.Add("One of the character's own effects: shows it in the 3D view (Parts), recolored with the character's Color");
        else if (IsCharacterEntry(p.Prototype)) lines.Add("The character's own parts (dodge, hover …): switch them off on Parts, recolor their effects on Color");
        else if (PowerColorBuild.IsPackage(p.Prototype)) lines.Add("A whole package, added with Add Package");
        else if (p.Animations.Count == 0) lines.Add("No animation of its own: its effects play over the idle");
        return string.Join("\n", lines);
    }

    /// <summary>The selected entry's name and icon beside the color controls.</summary>
    void ShowPowerTitle()
    {
        powerTitle.Text = powerList.SelectedItem is Fx.PowerList.Power p ? (BorrowOf(p.Prototype) is { } b ? $"{p.Name}  ·  Looks Like {b.DonorName}" : p.Name) : "";
        powerIcon.Visible = powerList.SelectedItem != null;
        powerIcon.Invalidate();
    }

    /// <summary>Before saving: the power colors into the draft's packages (PowerColorBuild.Apply). Returns the work folder.</summary>
    string? ApplyPowerColors(List<string> log, IReadOnlySet<string>? borrowed = null)
    {
        if (game == null) { if (draft.PowerColors.Any(e => !e.Color.IsNone)) throw new InvalidDataException("the game folder isn't set"); return null; }
        return PowerColorBuild.Apply(draft, editing, lib, game, ref powerDb, log, () => WorkFolder, borrowed);
    }
}
