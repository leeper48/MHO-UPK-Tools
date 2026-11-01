using System.Text.RegularExpressions;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The Editor's Voice tab: a costume's voice lines (play, turn off), another voice, Voice Shift.</summary>
sealed partial class ModEditorView
{
    // ---- Voice
    Control VoicePage()
    {
        voice = Ui.Grid(S, false, ("Situation", 210), ("Detail", 210), ("Settings", 210), ("What's Said", 0), ("Sound Event", 300), ("Package", 280));
        // (the whole event name shows, however long; the words take the room left: Kurt, 2026-10-31, they clipped it)
        voice.Columns["Sound Event"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;   // (measured in FillVoice: the grid's own sizing still clipped the longest)
        voice.Columns["What's Said"]!.MinimumWidth = (int)(240 * S);
        voice.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", Width = (int)(46 * S), SortMode = DataGridViewColumnSortMode.NotSortable });
        voice.Columns.Insert(1, new DataGridViewTextBoxColumn { Name = "play", HeaderText = "Play", Width = (int)(50 * S), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = Ui.Accent, SelectionForeColor = Ui.Accent } });
        voice.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && voice.Columns[e.ColumnIndex].Name == "play" && voice.Rows[e.RowIndex].Tag is VoiceLine pl) { if (pl.Sound) PlayVoice(pl); return; }
            if (e.RowIndex < 0 || voice.Columns[e.ColumnIndex].Name != "on" || voice.Rows[e.RowIndex].Tag is not VoiceLine l) return;
            if (l.Missing) return;   // the moved voice has no line here; the hero's own isn't in the package to turn on
            bool on = !IsOn(l);
            var audioWas = AudioNow();
            voiceWanted[(l.Package, l.Offset)] = on;
            AudioRecord($"Line {(on ? "On" : "Off")}: {l.Situation}", audioWas);
            voice.Rows[e.RowIndex].Cells["on"].Value = on;
            voice.Rows[e.RowIndex].DefaultCellStyle.ForeColor = on ? Ui.Text : Ui.Subtle;
            UpdateVoiceCount();
        };
        voice.SelectionChanged += (_, _) => { if (!voiceFilling) ShowVoiceShape(); };
        voiceFind.TextChanged += (_, _) => FillVoice();
        MhoPackageModifier.Gui.SearchBox.AddClear(voiceFind);
        Disposed += (_, _) => { VoiceAudio.Stop(); if (voiceWork != null) try { Directory.Delete(voiceWork, true); } catch (IOException) { } };
        Ui.Tip(voiceFind, "Show only lines whose situation, detail, settings, words or sound event have these words.");
        var find = new Label { Text = "Find", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 4, 0) };
        var allOn = Ui.FlatButton("Turn All On", () =>
        {
            var audioWas = AudioNow();
            foreach (var l in voiceLines) voiceWanted[(l.Package, l.Offset)] = true;
            AudioRecord("Turn All On", audioWas);
            FillVoice();
        }, tip: "Turn every line of the voice set back on.");
        var stop = Ui.FlatButton("Stop", () => { VoiceAudio.Stop(); voiceStatus.Text = ""; }, tip: "Stop the line that's playing.");
        var export = Ui.FlatButton("Export Lines", () => ExportLines(null), "Saves every line as a .WAV named after its sound event, with lines.csv saying what each one is and what's said. Change them in any audio or voice tool, or record your own, then use Replace Lines. Selected rows: only those.");
        var replace = Ui.FlatButton("Replace Lines", ReplaceLines, $"Pick a folder of {VoiceLines.Formats} files named as Export Lines names them: those lines of the voice are replaced by your files. You don't need all of them. Any rate, mono or stereo. Right-click a row for one line.");
        voice.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || voice.Rows[e.RowIndex].Tag is not VoiceLine) return;
            if (!voice.Rows[e.RowIndex].Selected) { voice.ClearSelection(); voice.Rows[e.RowIndex].Selected = true; }
            var picked = PickedLines();
            var menu = new ContextMenuStrip();
            if (picked.Count == 1) menu.Items.Add(new ToolStripMenuItem("Replace This Line", null, (_, _) => ReplaceOne(picked[0])) { ToolTipText = "Pick an audio file to play instead of this line." });
            menu.Items.Add(new ToolStripMenuItem(picked.Count == 1 ? "Export This Line" : $"Export {picked.Count} Lines", null, (_, _) => ExportLines(picked)) { ToolTipText = "Saves them as .WAV files." });
            if (picked.Any(IsOwnLine)) menu.Items.Add(new ToolStripMenuItem("Back to the Original Line", null, (_, _) => OriginalLines(picked)) { ToolTipText = "Takes your file out: the line plays as it did before." });
            if (picked.Any(l => RowOf(l) != null)) menu.Items.Add(new ToolStripMenuItem("Back to the Whole Voice", null, (_, _) => LinesBackToSet()) { ToolTipText = "Drops these lines' own settings: they follow the whole voice's again." });
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(voice, voice.PointToClient(Cursor.Position));
        };
        Button? another = null;
        another = Ui.FlatButton("Use Another Voice ▾", () => VoiceMenu(another!),
            tip: "Give the costume a whole voice from the game: its hero's own (so you can play and turn off its lines), another costume's voice such as Lady Deadpool or Spider-Gwen, or any hero's.");
        // Voice shift (Kurt, 2026-10-02): the sliders preview with ▶; Shift This Voice renders every line into the mod
        // (VoiceShiftBuild: a sound pack MHModManager 1.0.1 applies too); Remove Shift puts the original lines back.
        voicePitch = new LightSlider { Label = "Pitch", Min = -12, Max = 12, Step = 0.5f, Mark = 0, Format = v => v == 0 ? "0 st" : $"{v:+0.#;-0.#} st", Home = () => 0, Size = new Size((int)(132 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(10 * S), 0) };
        voiceFormant = new LightSlider { Label = "Formant", Min = -6, Max = 6, Step = 0.5f, Mark = 0, Format = v => v == 0 ? "0 st" : $"{v:+0.#;-0.#} st", Home = () => 0, Size = new Size((int)(132 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(10 * S), 0) };
        voiceWarmth = new LightSlider { Label = "Warmth", Min = -6, Max = 6, Step = 0.5f, Mark = 0, Format = v => v == 0 ? "0 dB" : $"{v:+0.#;-0.#} dB", Home = () => 0, Size = new Size((int)(132 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(10 * S), 0) };
        voicePitch.Value = 0; voiceFormant.Value = 0; voiceWarmth.Value = 0;
        voicePitch.Committed += () => SetLineShape(r => r.Pitch = voicePitch.Value);
        voiceFormant.Committed += () => SetLineShape(r => r.Formant = voiceFormant.Value);
        voiceWarmth.Committed += () => SetLineShape(r => r.Warmth = voiceWarmth.Value);
        // Filters (a user asked, 2026-10-16): 0-100 % each, previewed with ▶, applied with the shift
        LightSlider Fx(string label, string tip, Action<VoiceShiftEntry, float> set)
        {
            var sl = new LightSlider { Label = label, Min = 0, Max = 1, Step = 0.05f, Mark = 0, Format = v => v == 0 ? "Off" : $"{v * 100:0} %", Home = () => 0, Size = new Size((int)(165 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(8 * S), 0) };
            sl.Value = 0; Ui.Tip(sl, tip + " ▶ plays lines with it. Whole Voice: Apply to This Voice puts it into the mod. Selected Lines: it's set at once (double-click: off).");
            sl.Committed += () => SetLineShape(r => set(r, sl.Value));
            return sl;
        }
        voiceVolume = new LightSlider { Label = "Volume", Min = 0.05f, Max = 3, Step = 0.05f, Mark = 1, Format = v => $"{v * 100:0} %", Home = () => 1, Size = new Size((int)(165 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(8 * S), 0) };
        voiceVolume.Value = 1;
        voiceVolume.Committed += () => SetLineShape(r => r.Volume = Math.Abs(voiceVolume.Value - 1) < 0.001f ? null : voiceVolume.Value);
        Ui.Tip(voiceVolume, "How loud the voice plays; 100 % is the line as it is. Past full level the peaks are rounded off, so a line that's already at full level won't get much louder (double-click: back to 100 %).");
        voiceReverb = Fx("Reverb", "A room around the voice: small at low settings, a hall at 100 %.", (r, v) => r.Reverb = v);
        voiceEcho = Fx("Echo", "Repeats every quarter second, fading out (more repeats and louder as it goes up).", (r, v) => r.Echo = v);
        voiceDistortion = Fx("Distortion", "Drives the voice into soft clipping: gritty to heavily distorted.", (r, v) => r.Distortion = v);
        voiceLoFi = Fx("Lo-Fi", "Radio or old-speaker sound: a narrow band, fewer bits and a lower sample rate as it goes up.", (r, v) => r.LoFi = v);
        voiceChorus = Fx("Chorus", "Two slightly delayed, wavering copies of the voice: doubled, otherworldly.", (r, v) => r.Chorus = v);
        voiceRobot = Fx("Robot", "Ring modulation: a metallic, robotic voice.", (r, v) => r.Robot = v);
        Ui.Tip(voiceWarmth, "More (or less) of the low end, the voice's body. A raised voice can sound thin; a little warmth helps.");   // (a LightSlider starts at 1: it was made for the light level)
        Ui.Tip(voicePitch, "How many semitones higher or lower the voice is (double-click: back to 0). ▶ plays lines with these settings; Apply to This Voice puts them into the mod.");
        Ui.Tip(voiceFormant, "Moves the voice's formants (the size of the throat you hear) by semitones, apart from the pitch. Lower sounds bigger, higher smaller.");
        Button Preset(string text, float pitch, float formant, float warmth, string tip) => Ui.FlatButton(text, () => { voicePitch.Value = pitch; voiceFormant.Value = formant; voiceWarmth.Value = warmth; SetLineShape(r => { r.Pitch = pitch; r.Formant = formant; r.Warmth = warmth; }); }, tip);
        voiceApply = Ui.AccentButton("Apply to This Voice", ShiftVoice, "Shift and filter every line of the voice with these settings (about 30 seconds). Lines that are off stay off; sound effects in the voice set aren't changed; your own audio takes the volume and effects, not the shift. Save puts it into the mod: a sound pack MHModManager can install too.");
        voiceRemove = Ui.FlatButton("Remove Shift", () => _ = UnshiftVoice(), "Put the voice's original lines back, without the whole voice's settings (Save to keep it). Lines with settings of their own keep them.");
        voiceBackToSet = Ui.FlatButton("Back to the Whole Voice", LinesBackToSet, "Drops the selected lines' own settings: they follow the whole voice's again.");
        var noFx = Ui.FlatButton("No Effects", () => { foreach (var sl in new[] { voiceReverb, voiceEcho, voiceDistortion, voiceLoFi, voiceChorus, voiceRobot }) sl!.Value = 0; SetLineShape(r => r.Reverb = r.Echo = r.Distortion = r.LoFi = r.Chorus = r.Robot = 0); }, "Every filter off (the shift and volume stay).");
        voiceScopeSet = Ui.FlatButton("Whole Voice", () => SetVoiceScope(false), "The sliders are every line's settings. Nothing changes until Apply to This Voice. Lines with settings of their own keep them, unless you choose to override them.");
        voiceScopeRows = Ui.FlatButton("Selected Lines", () => SetVoiceScope(true), "The sliders are the selected lines' own settings, set as you move them, and built when you Save. A line's own settings take the place of the whole voice's. The Settings column says which each line follows.");
        var scopeRow = Toolbar(new Label { Text = "SETTINGS FOR", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 8, 0) }, voiceScopeSet, voiceScopeRows, voiceApply, voiceRemove, voiceBackToSet, voiceShiftNote);
        var shiftRow = Toolbar(new Label { Text = "VOICE SHIFT", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 8, 0) }, voicePitch, voiceFormant, voiceWarmth,
            Preset("Female → Male", -5, -2.5f, 0, "Pitch -5, formants -2.5: a starting point for a female voice to sound male."),
            Preset("Male → Female", 5, 2.5f, 2, "Pitch +5, formants +2.5, warmth +2 dB: a starting point for a male voice to sound female."),
            Preset("Original", 0, 0, 0, "Back to the line as it is (the filters stay as they are)."));
        // (fixed rows: a wrapping row doesn't grow in a docked panel, so its last sliders were hidden on a narrow window)
        var fxRow = Toolbar(new Label { Text = "EFFECTS", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 8, 0) }, voiceVolume, noFx, voiceReverb, voiceEcho);
        var fxRow2 = Toolbar(voiceDistortion, voiceLoFi, voiceChorus, voiceRobot);
        foreach (var row in new[] { scopeRow, shiftRow, fxRow, fxRow2 }) row.Padding = new Padding(0, 1, 0, 1);   // (room for the table)
        voiceShapeControls.AddRange([voicePitch, voiceFormant, voiceWarmth, voiceVolume, voiceReverb, voiceEcho, voiceDistortion, voiceLoFi, voiceChorus, voiceRobot, noFx]);
        voiceShapeControls.AddRange(shiftRow.Controls.OfType<Button>());
        if (draft.VoiceShifts.FirstOrDefault() is { } saved) ShowVoiceSliders(saved);
        ShowShiftNote();
        SetVoiceScope(false);
        var bars = new Panel { Dock = DockStyle.Top, AutoSize = true };
        var mainRow = Toolbar([find, voiceFind, allOn, stop, export, replace, another, .. AudioButtons(), voiceCount, voiceStatus]);
        voiceHintButton = Ui.AccentButton("Use the Hero's Voice", UseHeroVoice, "Add the hero's default costume package to the mod (the game's copy) and put the voice the game plays for it into that package, so its lines can be played, turned off and shifted.");
        voiceHintRow = Toolbar(voiceHint, voiceHintButton);
        voiceHintRow.Visible = false;
        bars.Controls.Add(voiceHintRow); bars.Controls.Add(fxRow2); bars.Controls.Add(fxRow); bars.Controls.Add(shiftRow); bars.Controls.Add(scopeRow); bars.Controls.Add(mainRow);   // (docked top: the last added is on top)
        return Page(voice, bars,
            "The costume's voice set: what the hero says in each situation. Click ▶ to hear a line. Untick a line to turn it off (for example a donor voice naming its own team); it's saved into the mod's package, and can be turned on again. A stock costume has no voice set of its own (it uses its hero's): Use Another Voice puts its hero's voice in, or another costume's or hero's.");
    }

    /// <summary>Packages given another voice in this edit: their source before (for Back to This Mod's Voice).</summary>
    readonly Dictionary<string, string> voiceBefore = new(StringComparer.OrdinalIgnoreCase);
    string? voiceWork;

    /// <summary>The draft's costume packages (UC__MarvelPlayer_&lt;Hero&gt;_&lt;Costume&gt;_SF) and team-up packages
    /// (UC__MarvelTeamUp_&lt;Name&gt;_SF, a user asked 2026-10-16): the ones a voice can be put into.</summary>
    /// <remarks>Also a hero's own package (UC__MarvelPlayer_&lt;Hero&gt;_SF, Kurt 2026-10-30: the Sentry mod ships only
    /// Vision's, Vision Classic has no costume package): its voice is the hero's, every costume without one of its own uses it.</remarks>
    List<(string File, string Source)> CostumePackages() =>
        [.. draft.Packages.Where(p => File.Exists(p.Source) && (Regex.IsMatch(p.File, @"^UC__MarvelPlayer_[A-Za-z0-9]+_[A-Za-z0-9_]+_SF\.upk$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(p.File, @"^UC__MarvelTeamUp_[A-Za-z0-9_]+_SF\.upk$", RegexOptions.IgnoreCase))),
         .. draft.Packages.Where(p => File.Exists(p.Source) && IsHeroPackage(p.File))];

    static bool IsHeroPackage(string file) => Regex.IsMatch(file, @"^UC__MarvelPlayer_[A-Za-z0-9]+_SF\.upk$", RegexOptions.IgnoreCase);

    /// <summary>The voice menu: per costume package, its hero's voices first, then every other hero's (submenus by hero).</summary>
    async void VoiceMenu(Control button)
    {
        var targets = CostumePackages();
        if (targets.Count == 0) { Dialog.Show(this, "A voice goes into a costume, team-up or hero package (UC__MarvelPlayer_<Hero>_<Costume>_SF.upk, UC__MarvelTeamUp_<Name>_SF.upk, UC__MarvelPlayer_<Hero>_SF.upk). Add one on the Packages tab first.", "No Costume or Team-Up Package", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (game == null) { Dialog.Show(this, "Set the game folder first (Settings → Change Game Folder): The voices come from the game's files.", "No Game Folder", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        voiceStatus.ForeColor = Ui.Subtle; voiceStatus.Text = "Loading the game's voices…";
        List<VoiceSet.Source> all;
        string cooked = game.Cooked;
        try { all = await Task.Run(() => VoiceSet.Sources(cooked)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { voiceStatus.Text = ""; Dialog.Show(this, ex.Message, "Can't Read the Voices", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        voiceStatus.Text = "";
        if (IsDisposed) return;
        var menu = new ContextMenuStrip();
        foreach (var (file, source) in targets)
        {
            ToolStripItemCollection items = menu.Items;
            bool heroOwn = IsHeroPackage(file);
            string label = heroOwn ? $"{file} (the Hero's Own Voice)" : file;
            if (targets.Count > 1) { var sub = new ToolStripMenuItem(label); menu.Items.Add(sub); items = sub.DropDownItems; }
            if (heroOwn)
                items.Add(new ToolStripMenuItem("The hero's own voice: every costume without a voice of its own uses it") { Enabled = false, ToolTipText = file });
            string hero = (VoiceSet.HeroOf(file) ?? VoiceSet.TeamUpOf(file) ?? "").ToLowerInvariant();
            bool teamUp = VoiceSet.TeamUpOf(file) != null;
            bool Mine(VoiceSet.Source v) => hero.Length > 0 && v.TeamUp == teamUp && (v.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase) || v.Hero.StartsWith(hero, StringComparison.OrdinalIgnoreCase) || hero.StartsWith(v.Hero.ToLowerInvariant()));
            var own = all.Where(Mine).ToList();
            foreach (var v in own) items.Add(VoiceItem(file, v));
            if (own.Count > 0) items.Add(new ToolStripSeparator());
            var others = new ToolStripMenuItem("Other Heroes");
            foreach (var g in all.Where(v => !Mine(v) && !v.TeamUp).GroupBy(v => v.Title.Split(" · ")[0]))
            {
                if (g.Count() == 1) { others.DropDownItems.Add(VoiceItem(file, g.First())); continue; }
                var h = new ToolStripMenuItem(g.Key);
                foreach (var v in g) h.DropDownItems.Add(VoiceItem(file, v));
                others.DropDownItems.Add(h);
            }
            items.Add(others);
            // team-ups' voices (Angel, Jessica Jones …): for a hero's costume or another team-up
            var teams = new ToolStripMenuItem("Team-Ups");
            foreach (var v in all.Where(v => v.TeamUp && !Mine(v))) teams.DropDownItems.Add(VoiceItem(file, v));
            if (teams.DropDownItems.Count > 0) items.Add(teams);
            if (voiceBefore.ContainsKey(file))
            {
                items.Add(new ToolStripSeparator());
                items.Add(new ToolStripMenuItem("Back to This Mod's Voice", null, (_, _) => UseVoice(file, null)));
            }
        }
        Ui.ShowUnder(menu, button);
    }

    ToolStripMenuItem VoiceItem(string file, VoiceSet.Source v) => new(v.Title, null, (_, _) => UseVoice(file, v)) { ToolTipText = Path.GetFileName(v.File) };

    /// <summary>Puts a stock voice into a costume package of the draft (VoiceSet.Replace, verified, in a work folder until Save), or back to the mod's own.</summary>
    async void UseVoice(string file, VoiceSet.Source? v, AudioState? audioWas = null)
    {
        audioWas ??= AudioNow();
        int k = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (k < 0) return;
        if (v == null)
        {
            if (voiceBefore.Remove(file, out string? before)) draft.Packages[k] = (file, before);
        }
        else
        {
            string from = voiceBefore.TryGetValue(file, out string? b) ? b : draft.Packages[k].Source;
            voiceStatus.ForeColor = Ui.Subtle; voiceStatus.Text = "Copying " + v.Title + "…";
            var log = new List<string>();
            byte[] bytes;
            try { bytes = await Task.Run(() => VoiceSet.Replace(from, v.File, log)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException)
            { voiceStatus.Text = ""; Dialog.Show(this, $"{v.Title}'s voice couldn't be copied into {file}: {ex.Message}", "Voice Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (IsDisposed) return;
            voiceWork ??= Path.Combine(lib.DataFolder, "voice-work-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(voiceWork);
            string path = Path.Combine(Directory.CreateDirectory(Path.Combine(voiceWork, Guid.NewGuid().ToString("N")[..6])).FullName, file);   // (its own name: see UseWorkCopy)
            File.WriteAllBytes(path, bytes);
            voiceBefore.TryAdd(file, from);
            draft.Packages[k] = (file, path);
            voiceStatus.Text = "Voice: " + v.Title;
        }
        // A shift was made from the voice that's gone: dropped.
        draft.VoiceShifts.RemoveAll(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase));
        draft.SoundPacks.RemoveAll(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase));
        ShowShiftNote(); RefreshSounds();
        // The lines are new: what was turned off applied to the old set.
        draft.VoiceOff.RemoveAll(o => o.Package.Equals(file, StringComparison.OrdinalIgnoreCase));
        foreach (var key in voiceWanted.Keys.Where(x => x.Item1.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList()) voiceWanted.Remove(key);
        voiceWavs.Clear();
        RefreshPackages();
        AudioRecord(v == null ? "Back to This Mod's Voice" : "Voice: " + v.Title, audioWas);
    }

    readonly Label voiceStatus = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(14, 8, 0, 0) };
    readonly Dictionary<string, byte[]> voiceWavs = new(StringComparer.OrdinalIgnoreCase);
    int voicePlayId;

    /// <summary>Plays a line (VoiceAudio: the mod's sound pack, else the game's sound files), found and decoded in the background.</summary>
    LightSlider? voicePitch, voiceFormant, voiceWarmth, voiceVolume, voiceReverb, voiceEcho, voiceDistortion, voiceLoFi, voiceChorus, voiceRobot;
    Button voiceScopeSet = null!, voiceScopeRows = null!, voiceApply = null!, voiceRemove = null!, voiceBackToSet = null!;
    readonly List<Control> voiceShapeControls = [];
    /// <summary>The sliders are the selected lines' own settings, not the whole voice's.</summary>
    bool voiceRowScope, voiceFilling;
    /// <summary>The whole voice's sliders, while they show the selected lines.</summary>
    VoiceShiftEntry voiceSetKept = new();
    /// <summary>Each package's entry as last built (a change after it is built at Save).</summary>
    readonly Dictionary<string, string> voiceBuilt = new(StringComparer.OrdinalIgnoreCase);
    readonly Label voiceHint = new() { AutoSize = true, ForeColor = Ui.OverrideAmber, Padding = new Padding(0, 8, 8, 0) };
    Button? voiceHintButton;
    FlowLayoutPanel? voiceHintRow;
    (string CostumeFile, string CostumeName, VoiceSet.Source Voice)? heroVoice;
    /// <summary>The mod's own costume package the hero's voice goes into (else the default costume's, added).</summary>
    string? heroVoiceInto;
    int hintRun;

    bool HasVoiceSlot(string file)
    {
        var src = draft.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Source;
        try { return src != null && File.Exists(src) && VoiceSet.Slot(MhoPackageModifier.Package.Open(src)) >= 0; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { return false; }
    }

    /// <summary>
    /// The mod's own costume package for <paramref name="hero"/> (UC__MarvelPlayer_&lt;hero&gt;_&lt;costume&gt;_SF, not the base,
    /// not <paramref name="exceptDefault"/>) with a voice slot: the costume the mod is for. A user, 2026-10-22: White Tiger on
    /// Iron Fist Classic added his default costume (White) and shifted its voice; Classic plays its own slot, empty, so
    /// the hero's base voice: in game the lines stayed male.
    /// </summary>
    string? OwnCostumeFile(string hero, string exceptDefault) => draft.Packages.Select(p => p.File)
        .FirstOrDefault(f => f.StartsWith($"UC__MarvelPlayer_{hero}_", StringComparison.OrdinalIgnoreCase) && !f.Equals($"UC__MarvelPlayer_{hero}_SF.upk", StringComparison.OrdinalIgnoreCase)
            && !f.Equals(exceptDefault, StringComparison.OrdinalIgnoreCase) && HasVoiceSlot(f));

    /// <summary>
    /// No sound lines in the mod's packages (Scream on Carnage's base package: one banter target): says where the hero's
    /// voice is in the game (VoiceSet.HeroVoice) and offers to put it into the default costume's package.
    /// </summary>
    async void UpdateVoiceHint()
    {
        if (voiceHintRow == null) return;
        int run = ++hintRun;
        heroVoice = null;
        string? hero = draft.Packages.Select(p => p.File).Where(f => !f.StartsWith("UC__MarvelPlayerAudio_", StringComparison.OrdinalIgnoreCase))
            .Select(VoiceSet.HeroOf).FirstOrDefault(h => h != null);
        if (voiceLines.Any(l => l.Sound) || hero == null || game == null) { voiceHintRow.Visible = false; return; }
        string root = game.Root, cooked = game.Cooked;
        var found = await Task.Run(() => { try { return VoiceSet.HeroVoice(root, cooked, hero); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; } });
        if (IsDisposed || run != hintRun) return;
        heroVoice = found;
        if (found is not { } f) { voiceHintRow.Visible = false; return; }
        string heroName = f.Voice.Title.Split(" · ")[0];
        heroVoiceInto = OwnCostumeFile(hero, f.CostumeFile);
        if (heroVoiceInto != null)
        {
            voiceHint.Text = $"No voice lines in this mod's packages. In the game, this costume plays {heroName}'s voice \"{f.Voice.Title}\": copied into {heroVoiceInto}, its lines can be played, turned off and shifted for this costume.";
            voiceHintButton!.Text = "Use " + f.Voice.Title;
            voiceHintRow.Visible = true;
            return;
        }
        bool have = draft.Packages.Any(p => p.File.Equals(f.CostumeFile, StringComparison.OrdinalIgnoreCase));
        voiceHint.Text = $"No voice lines in this mod's packages. In the game, {heroName}'s {f.CostumeName} costume plays the voice \"{f.Voice.Title}\""
            + (have ? "." : $" ({f.CostumeFile}, not in this mod).");
        voiceHintButton!.Text = have ? "Use " + f.Voice.Title : $"Add {f.CostumeName} and Use Its Voice";
        voiceHintRow.Visible = true;
    }

    void UseHeroVoice()
    {
        if (heroVoice is not { } f || game == null) return;
        if (heroVoiceInto != null) { UseVoice(heroVoiceInto, f.Voice); return; }
        var audioWas = AudioNow();
        if (!draft.Packages.Any(p => p.File.Equals(f.CostumeFile, StringComparison.OrdinalIgnoreCase)))
        {
            string src = StockFiles.For(game.Cooked, f.CostumeFile);
            if (game.HasStockList && !game.MatchesStock(f.CostumeFile, src))
            {
                Dialog.Show(this, $"The game's {f.CostumeFile} has been changed by another mod and there's no clean copy, so it can't be added as it is.", "No Clean Copy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            draft.Packages.Add((f.CostumeFile, src));
            RefreshPackages();
        }
        UseVoice(f.CostumeFile, f.Voice, audioWas);
    }
    readonly Label voiceShiftNote = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 0, 0) };
    bool shifting;

    VoiceShiftEntry Sliders => new()
    {
        Pitch = voicePitch?.Value ?? 0, Formant = voiceFormant?.Value ?? 0, Warmth = voiceWarmth?.Value ?? 0,
        Volume = voiceVolume == null || Math.Abs(voiceVolume.Value - 1) < 0.001f ? null : voiceVolume.Value,
        Reverb = voiceReverb?.Value ?? 0, Echo = voiceEcho?.Value ?? 0, Distortion = voiceDistortion?.Value ?? 0, LoFi = voiceLoFi?.Value ?? 0, Chorus = voiceChorus?.Value ?? 0, Robot = voiceRobot?.Value ?? 0,
    };
    /// <summary>The whole voice's settings, whichever the sliders show.</summary>
    VoiceShiftEntry VoiceSetSliders => voiceRowScope ? voiceSetKept : Sliders;

    static bool Same(VoiceShiftEntry a, VoiceShiftEntry b) => a.Key == b.Key;

    void ShowVoiceSliders(VoiceShiftEntry s)
    {
        foreach (var (sl, v) in new[] { (voicePitch, s.Pitch), (voiceFormant, s.Formant), (voiceWarmth, s.Warmth), (voiceVolume, s.Volume ?? 1), (voiceReverb, s.Reverb), (voiceEcho, s.Echo), (voiceDistortion, s.Distortion), (voiceLoFi, s.LoFi), (voiceChorus, s.Chorus), (voiceRobot, s.Robot) })
            if (sl != null) sl.Value = v;
    }

    /// <summary>Whole Voice or Selected Lines: what the sliders are.</summary>
    void SetVoiceScope(bool rows)
    {
        if (rows != voiceRowScope)
        {
            if (rows) voiceSetKept = Sliders; else ShowVoiceSliders(voiceSetKept);
            voiceRowScope = rows;
        }
        Ui.Lit(voiceScopeSet, !rows); Ui.Lit(voiceScopeRows, rows);
        voiceApply.Visible = voiceRemove.Visible = !rows;
        voiceBackToSet.Visible = rows;
        ShowVoiceShape();
    }

    /// <summary>The lines the sliders work on: the selected ones with sound.</summary>
    List<VoiceLine> PickedLinesWithSound() => voice == null ? [] : [.. PickedLines().Where(l => l.Sound && !l.Missing)];

    VoiceShiftEntry? RowOf(VoiceLine l) => draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(l.Package, StringComparison.OrdinalIgnoreCase))?.LineOf(VoiceLines.Leaf(l.Event));

    /// <summary>Selected Lines: the sliders show the first selected line; with none selected they're off.</summary>
    void ShowVoiceShape()
    {
        if (voice == null || voiceBackToSet == null) return;
        var picked = PickedLinesWithSound();
        if (voiceRowScope)
        {
            if (picked.Count > 0)
            {
                var e = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(picked[0].Package, StringComparison.OrdinalIgnoreCase));
                ShowVoiceSliders(e?.For(VoiceLines.Leaf(picked[0].Event)) ?? new VoiceShiftEntry());
            }
            else ShowVoiceSliders(new VoiceShiftEntry());
        }
        foreach (var c in voiceShapeControls) c.Enabled = !voiceRowScope || picked.Count > 0;
        voiceBackToSet.Enabled = picked.Any(l => RowOf(l) != null);
    }

    /// <summary>A slider moved in Selected Lines: every selected line's own settings (a line's first change starts from what it
    /// plays with now). Built at Save; a step of the Audio tab's Undo.</summary>
    void SetLineShape(Action<VoiceShiftEntry> set)
    {
        if (!voiceRowScope) return;
        var picked = PickedLinesWithSound();
        if (picked.Count == 0) return;
        var was = AudioNow();
        foreach (var g in picked.GroupBy(l => l.Package, StringComparer.OrdinalIgnoreCase))
        {
            var e = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (e == null) draft.VoiceShifts.Add(e = new VoiceShiftEntry { Package = g.Key });
            e.Lines ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (string leaf in g.Select(l => VoiceLines.Leaf(l.Event)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var row = e.LineOf(leaf) ?? e.For(leaf);
                row.Package = g.Key;
                set(row);
                e.Lines.Remove(leaf);
                if (row.Any || e.Any) e.Lines[leaf] = row;   // (nothing set and nothing to differ from: it follows the set)
            }
        }
        draft.VoiceShifts.RemoveAll(x => x.Lines is { Count: 0 } && !x.Any && x.Replaced is not { Count: > 0 });
        foreach (var x in draft.VoiceShifts) if (x.Lines is { Count: 0 }) x.Lines = null;
        AudioRecord(picked.Count == 1 ? "This Line: " + picked[0].Situation : $"Selected Lines: {picked.Count}", was);
        ShowLineSettings();
        voiceBackToSet.Enabled = picked.Any(l => RowOf(l) != null);
        ShowShiftNote();
        voiceShiftNote.Text += " · Not Saved Yet";
    }

    /// <summary>The selected lines follow the whole voice again.</summary>
    void LinesBackToSet()
    {
        var picked = PickedLinesWithSound();
        var was = AudioNow();
        foreach (var l in picked)
        {
            var e = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(l.Package, StringComparison.OrdinalIgnoreCase));
            if (e?.Lines == null) continue;
            foreach (string k in e.Lines.Keys.Where(k => k.Equals(VoiceLines.Leaf(l.Event), StringComparison.OrdinalIgnoreCase)).ToList()) e.Lines.Remove(k);
            if (e.Lines.Count == 0) e.Lines = null;
        }
        draft.VoiceShifts.RemoveAll(x => x.Lines == null && !x.Any && x.Replaced is not { Count: > 0 } && !voiceBuilt.ContainsKey(x.Package) && !(editing?.Manifest.VoiceShifts?.Any(w => w.Package.Equals(x.Package, StringComparison.OrdinalIgnoreCase)) ?? false));
        AudioRecord("Back to the Whole Voice", was);
        ShowLineSettings();
        ShowVoiceShape();
        ShowShiftNote();
        voiceShiftNote.Text += " · Not Saved Yet";
    }

    /// <summary>A line's Settings cell: what it follows (the column that tells a whole set's change from a row's own).</summary>
    string LineSetting(VoiceLine l)
    {
        if (!l.Sound || l.Missing) return "";
        if (RowOf(l) is { } row) return "This Line" + (row.Any ? ": " + ShapeText(row) : ": As It Is");
        var e = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(l.Package, StringComparison.OrdinalIgnoreCase));
        if (e?.For(VoiceLines.Leaf(l.Event)).Any != true) return "";
        return IsOwnLine(l) ? "Whole Voice (No Shift)" : "Whole Voice";
    }

    /// <summary>"Pitch +2, Volume 60 %, Reverb 40 %" for what's set.</summary>
    static string ShapeText(VoiceShiftEntry v) => (((v.Pitch != 0 ? $", Pitch {v.Pitch:+0.#;-0.#}" : "") + (v.Formant != 0 ? $", Formant {v.Formant:+0.#;-0.#}" : "") + (v.Warmth != 0 ? $", Warmth {v.Warmth:+0.#;-0.#}" : "")
        + (v.Volume is float g && Math.Abs(g - 1) > 0.001f ? $", Volume {g * 100:0} %" : "") + EffectsText(v)) is { Length: > 0 } t ? t[2..] : "");

    static void PaintSetting(DataGridViewCell c, string text)
    {
        c.Value = text;
        c.Style.ForeColor = c.Style.SelectionForeColor = text.StartsWith("This", StringComparison.Ordinal) ? Ui.OverrideAmber : Ui.Enabled;
    }

    void ShowLineSettings()
    {
        foreach (DataGridViewRow r in voice!.Rows) if (r.Tag is VoiceLine l) PaintSetting(r.Cells["Settings"], LineSetting(l));
    }

    void ShowShiftNote()
    {
        var v = draft.VoiceShifts.FirstOrDefault();
        voiceShiftNote.ForeColor = v == null ? Ui.Subtle : Ui.Enabled;
        int own = draft.VoiceShifts.Sum(x => x.Replaced?.Count ?? 0), rows = draft.VoiceShifts.Sum(x => x.Lines?.Count ?? 0);
        voiceShiftNote.Text = (v?.Any != true ? "No Whole-Voice Settings" : "Whole Voice: " + ShapeText(v))
            + (own > 0 ? $" · {own} Line{(own == 1 ? "" : "s")} Replaced" : "")
            + (rows > 0 ? $" · {rows} Line{(rows == 1 ? "" : "s")} With Own Settings" : "");
    }

    /// <summary>", Reverb 40 %, Echo 20 %" for the filters that are on.</summary>
    static string EffectsText(VoiceShiftEntry v) => string.Concat(new (string Name, float A)[] { ("Reverb", v.Reverb), ("Echo", v.Echo), ("Distortion", v.Distortion), ("Lo-Fi", v.LoFi), ("Chorus", v.Chorus), ("Robot", v.Robot) }
        .Where(f => f.A > 0.001f).Select(f => $", {f.Name} {f.A * 100:0} %"));

    /// <summary>Shift This Voice: every costume package's voice rendered (VoiceShiftBuild) into the work folder; Save writes it.</summary>
    void ShiftVoice() => _ = ShiftVoiceAsync();

    /// <summary>For --voice-shift-tab-test: the sliders set, then Shift This Voice (or Remove Shift).</summary>
    internal Task ShiftForTest(float pitch, float formant, float warmth)
    {
        voicePitch!.Value = pitch; voiceFormant!.Value = formant; voiceWarmth!.Value = warmth;
        // MHO_TEST_VOICEFX=reverb=0.4,echo=0.3 …: the filters too
        foreach (string kv in (Environment.GetEnvironmentVariable("MHO_TEST_VOICEFX") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            float v = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            var sl = p[0].ToLowerInvariant() switch { "reverb" => voiceReverb, "echo" => voiceEcho, "distortion" => voiceDistortion, "lofi" => voiceLoFi, "chorus" => voiceChorus, "robot" => voiceRobot, _ => null };
            if (sl != null) sl.Value = v;
        }
        return ShiftVoiceAsync();
    }
    internal void RemoveShiftForTest() => RemoveShift();

    /// <summary>For --voice-shift-tab-test: when the hint offers the hero's voice, use it (and wait for the lines). The hint's text, or null.</summary>
    internal async Task<string?> HeroVoiceForTest()
    {
        for (int i = 0; i < 300 && heroVoice == null && voiceLines.All(l => !l.Sound); i++) await Task.Delay(100);
        if (heroVoice == null) return null;
        string hint = voiceHint.Text + " [" + voiceHintButton!.Text + "]";
        UseHeroVoice();
        for (int i = 0; i < 600 && !voiceLines.Any(l => l.Sound); i++) await Task.Delay(100);
        return hint;
    }
    internal string ShiftNote => voiceShiftNote.Text;

    /// <summary>Test (--voice-shape-test): a line's own settings and the whole voice through the tab, with builds; nothing is saved.</summary>
    internal async Task<int> TestVoiceShape(Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        RefreshVoice();
        await HeroVoiceForTest();
        var lines = voiceLines.Where(l => l.Sound && !l.Missing && !l.Off).DistinctBy(l => VoiceLines.Leaf(l.Event), StringComparer.OrdinalIgnoreCase).ToList();
        if (lines.Count < 4) { Check(false, "the mod has a voice"); return fails; }
        var (a, b, c) = (lines[0], lines[1], lines[2]);
        void Pick(params VoiceLine[] rows)
        {
            voice!.ClearSelection();
            foreach (DataGridViewRow row in voice.Rows) if (row.Tag is VoiceLine l && rows.Contains(l)) row.Selected = true;
        }
        VoiceLine Now(VoiceLine l) => voiceLines.First(x => x.Offset == l.Offset && x.Package == l.Package);
        string Cell(VoiceLine l) => voice!.Rows.Cast<DataGridViewRow>().First(x => x.Tag is VoiceLine y && y.Offset == l.Offset && y.Package == l.Package).Cells["Settings"].Value as string ?? "";
        Dictionary<string, byte[]> Wems() => SoundPack.Load(draft.SoundPacks.First(p => Path.GetFileName(p).Equals(VoiceShiftBuild.PackName(a.Package), StringComparison.OrdinalIgnoreCase))).Wems.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        static float Peak(byte[] wem) => WavPcm.Read(VoiceAudio.ToWav(wem)).Channels[0].Max(MathF.Abs);
        string Name(VoiceLine l) => VoiceLines.Leaf(l.Event) + VoiceShiftBuild.Suffix + ".wem";
        SetVoiceScope(true);
        Pick();
        Check(!voiceVolume!.Enabled, "Selected Lines, nothing selected: the sliders are off");
        Pick(a);
        Check(voiceVolume.Enabled, "a line selected: the sliders are on");
        voiceVolume.TestKey(Keys.Left);
        Check(RowOf(a)?.Volume is > 0.94f and < 0.96f, "the line has a volume of its own");
        Check(Cell(a).StartsWith("This Line: Volume 95 %"), $"its Settings cell says so ({Cell(a)})");
        Check(Cell(b) == "", "the next line doesn't");
        AudioUndo();
        Check(RowOf(a) == null && Cell(a) == "", "undo: gone");
        AudioRedo();
        Check(RowOf(a) != null, "redo");
        SetLineShape(r => r.Volume = 0.5f);
        SetVoiceScope(false);
        Check(voiceVolume.Enabled && Math.Abs(voiceVolume.Value - 1) < 0.001f, "Whole Voice: the sliders are the whole voice's");
        await ShiftForTest(2, 0, 0);   // (the test's Dialog answers Keep Their Settings)
        a = Now(a); b = Now(b); c = Now(c);
        Check(draft.VoiceShifts.FirstOrDefault() is { Pitch: 2 } e && e.LineOf(VoiceLines.Leaf(a.Event))?.Pitch == 0, "the whole voice applied: the line keeps its own");
        Check(Cell(b) == "Whole Voice" && Cell(a).StartsWith("This Line"), $"Settings: {Cell(b)}, {Cell(a)}");
        var one = Wems();
        float plain = Peak(VoiceAudio.Wem(VoiceShiftBuild.Original(a.Event), [], game!.Cooked).Wem);
        Check(Math.Abs(Peak(one[Name(a)]) / plain - 0.5f) < 0.08f, $"the line's volume is in the pack: peak {plain:0.000} → {Peak(one[Name(a)]):0.000}");
        Check(voiceShiftNote.Text.Contains("Whole Voice: Pitch +2") && voiceShiftNote.Text.Contains("1 Line With Own Settings"), $"the note ({voiceShiftNote.Text})");
        // one more line by itself: built at Save, the others not encoded again
        SetVoiceScope(true);
        Pick(c);
        Check(Math.Abs(voicePitch!.Value - 2) < 0.001f, "Selected Lines shows what the line plays with");
        SetLineShape(r => r.Robot = 0.5f);
        Check(RowOf(c) is { Pitch: 2, Robot: 0.5f }, "a line leaves the set from its settings");
        var said = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        BuildVoiceRows(said);
        long ms = sw.ElapsedMilliseconds;
        var two = Wems();
        var changed = one.Keys.Where(k => !two[k].SequenceEqual(one[k])).ToList();
        Check(changed.Count == 1 && changed[0].Equals(Name(c), StringComparison.OrdinalIgnoreCase), $"Save's build: {changed.Count} of {one.Count} lines encoded again, in {ms} ms ({string.Join(", ", changed.Take(3))})");
        said.Clear(); BuildVoiceRows(said);
        Check(said.Count == 0, "nothing changed: no build");
        LinesBackToSet();
        Check(RowOf(c) == null && Cell(Now(c)) == "Whole Voice", "Back to the Whole Voice");
        BuildVoiceRows(said);
        Check(Wems()[Name(c)].SequenceEqual(one[Name(c)]), "and it's the whole voice's line again, byte for byte");
        await UnshiftVoice();
        Check(draft.VoiceShifts.Count == 1 && RowOf(Now(a)) != null && !draft.VoiceShifts[0].Any && Cell(Now(b)) == "", "Remove Shift: the line keeps its own settings, the rest are the game's");
        Check(Wems().Count == 1, $"the pack holds that line ({Wems().Count})");
        Environment.SetEnvironmentVariable("MHO_EXTMM_TEST_CHOICE", "1");
        await ShiftForTest(1, 0, 0);
        Environment.SetEnvironmentVariable("MHO_EXTMM_TEST_CHOICE", "0");
        Check(draft.VoiceShifts.All(x => x.Lines == null) && Cell(Now(a)) == "Whole Voice", "Override Them: the whole voice replaces the line's");
        await ShiftForTest(0, 0, 0);
        return fails;
    }

    /// <summary>--audio-snapshot: a shifted voice with two lines of their own.</summary>
    internal async Task SnapVoice()
    {
        RefreshVoice();
        await HeroVoiceForTest();
        await ShiftForTest(2, 0, 0);
        SetVoiceScope(true);
        voice!.ClearSelection();
        foreach (DataGridViewRow row in voice.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is VoiceLine { Sound: true, Missing: false, Off: false }).Skip(1).Take(2)) row.Selected = true;
        SetLineShape(r => { r.Volume = 0.7f; r.Reverb = 0.4f; });
        ShowVoiceShape();
    }

    /// <summary>The lines picked in the table.</summary>
    List<VoiceLine> PickedLines() => [.. voice!.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => r.Tag).OfType<VoiceLine>()];

    /// <summary>Is this line replaced by a file of the user's?</summary>
    bool IsOwnLine(VoiceLine l) => draft.VoiceShifts.Any(x => x.Package.Equals(l.Package, StringComparison.OrdinalIgnoreCase) && x.IsReplaced(VoiceLines.Leaf(l.Event)));

    /// <summary>Export Lines: <paramref name="only"/>, else the rows selected if there are several, else all.</summary>
    async void ExportLines(List<VoiceLine>? only)
    {
        if (game == null) return;
        var picked = only ?? (voice!.SelectedRows.Count > 1 ? PickedLines() : voiceLines);
        if (!picked.Any(l => l.Sound)) { Dialog.Show(this, "No package of this mod has a voice set. Use Another Voice gives a costume one first.", "No Voice to Export", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        using var d = new FolderBrowserDialog { Description = "Folder for the lines (.WAV files and lines.csv)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string folder = d.SelectedPath, cooked = game.Cooked;
        var packs = draft.SoundPacks.ToList();
        var lines = picked.ToList();
        var progress = new Progress<string>(m => { voiceStatus.ForeColor = Ui.Subtle; voiceStatus.Text = m; });
        var (written, failed) = await Task.Run(() => VoiceLines.Export(lines, packs, cooked, folder, progress));
        if (IsDisposed) return;
        voiceStatus.ForeColor = Ui.Subtle;
        voiceStatus.Text = $"{written} Line{(written == 1 ? "" : "s")} Exported";
        if (failed.Count > 0) Dialog.ShowLog(this, $"{written} exported. These have no sound (silent in the game too):" + Environment.NewLine + string.Join(Environment.NewLine, failed), "Lines Exported");
    }

    /// <summary>Replace Lines: a folder of files named after the lines.</summary>
    void ReplaceLines()
    {
        using var d = new FolderBrowserDialog { Description = "Folder with your audio files, named as Export Lines names them", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var lines = voiceLines.Where(l => l.Sound && !l.Missing && !l.Off).ToList();
        var (matched, unknown) = VoiceLines.Match(d.SelectedPath, lines.Select(l => VoiceLines.Leaf(l.Event)));
        if (matched.Count == 0) { Dialog.Show(this, "No file there is named after a line that's on in this voice. Export Lines shows the names.", "Nothing to Replace", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (unknown.Count > 0 && Dialog.Show(this, $"{matched.Count} line(s) will be replaced.{Environment.NewLine}{Environment.NewLine}{unknown.Count} file(s) aren't named after a line and are left out:{Environment.NewLine}{string.Join(Environment.NewLine, unknown.Take(12).Select(u => "  • " + u))}{(unknown.Count > 12 ? Environment.NewLine + "  …" : "")}",
                "Replace Lines", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _ = PutLines(lines.Where(l => matched.ContainsKey(VoiceLines.Leaf(l.Event))).ToList(), matched, $"Replace {matched.Count} Line{(matched.Count == 1 ? "" : "s")}");
    }

    void ReplaceOne(VoiceLine l)
    {
        using var d = new OpenFileDialog { Title = "A line to play instead of " + VoiceLines.Leaf(l.Event), Filter = VoiceLines.FileFilter };
        if (d.ShowDialog(this) == DialogResult.OK) _ = PutLines([l], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [VoiceLines.Leaf(l.Event)] = d.FileName }, "Replace Line: " + l.Situation);
    }

    /// <summary>The files into the mod, per package (what's already replaced or shifted stays).</summary>
    internal async Task PutLines(List<VoiceLine> lines, IReadOnlyDictionary<string, string> files, string label)
    {
        Dictionary<string, byte[]> wavs;
        var bad = new List<string>();
        try
        {
            wavs = await Task.Run(() =>
            {
                var w = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var (leaf, file) in files)
                    try { w[leaf] = VoiceLines.Load(file); }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException or UnauthorizedAccessException) { bad.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
                return w;
            });
        }
        finally { }
        if (IsDisposed) return;
        var entries = new Dictionary<string, VoiceShiftEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in lines.Where(l => wavs.ContainsKey(VoiceLines.Leaf(l.Event))).GroupBy(l => l.Package, StringComparer.OrdinalIgnoreCase))
        {
            var e = SavedShift(g.Key);
            e.Replaced = [.. (e.Replaced ?? []).Concat(g.Select(l => VoiceLines.Leaf(l.Event))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            entries[g.Key] = e;
        }
        if (entries.Count > 0) await BuildVoice(entries, wavs, label);
        if (bad.Count > 0) Dialog.ShowLog(this, "These files couldn't be read:" + Environment.NewLine + string.Join(Environment.NewLine, bad), "Some Lines Not Replaced");
    }

    /// <summary>Back to the Original Line: the user's files out of these lines.</summary>
    async void OriginalLines(List<VoiceLine> lines)
    {
        var entries = new Dictionary<string, VoiceShiftEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in lines.Where(IsOwnLine).GroupBy(l => l.Package, StringComparer.OrdinalIgnoreCase))
        {
            var e = SavedShift(g.Key);
            var gone = g.Select(l => VoiceLines.Leaf(l.Event)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            e.Replaced = e.Replaced?.Where(x => !gone.Contains(x)).ToList();
            foreach (string k in gone) e.Lines?.Remove(k);
            entries[g.Key] = e;
        }
        await BuildVoice(entries, null, lines.Count == 1 ? "Back to the Original: " + lines[0].Situation : $"Back to the Original: {lines.Count} Lines");
    }

    /// <summary>A copy of the package's saved shift and replaced lines (none: an empty one).</summary>
    VoiceShiftEntry SavedShift(string file)
    {
        var s = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase));
        return s?.Clone() ?? new VoiceShiftEntry { Package = file };
    }

    /// <summary>Remove Shift: the lines the user replaced stay.</summary>
    async Task UnshiftVoice()
    {
        var kept = draft.VoiceShifts.Where(x => x.Replaced is { Count: > 0 } || x.Lines is { Count: > 0 }).ToList();
        if (kept.Count == 0) { RemoveShift(); return; }
        await BuildVoice(kept.ToDictionary(x => x.Package, x => { var e = new VoiceShiftEntry { Package = x.Package, Replaced = x.Replaced?.ToList(), Lines = x.Clone().Lines }; return e; }, StringComparer.OrdinalIgnoreCase), null, "Remove Shift");
    }

    async Task ShiftVoiceAsync()
    {
        if (shifting || game == null) return;
        var targets = draft.Packages.Where(p => File.Exists(p.Source) && VoiceSet.Read(p.File, p.Source, draft.VoiceOff).Any(l => l.Sound)).ToList();
        if (targets.Count == 0) { Dialog.Show(this, "No package of this mod has a voice set. Use Another Voice gives a costume one first.", "No Voice to Shift", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var set = Sliders;
        // Lines with settings of their own: the user says whether the whole voice overrides them.
        int rows = draft.VoiceShifts.Sum(x => x.Lines?.Count ?? 0);
        bool over = false;
        if (rows > 0)
        {
            int pick = Dialog.Choose(this, $"{rows} line{(rows == 1 ? " has" : "s have")} settings of {(rows == 1 ? "its" : "their")} own. Should the whole voice replace them?", "Lines With Their Own Settings", "Keep Their Settings", "Override Them", "Cancel");
            if (pick == 2) return;
            over = pick == 1;
        }
        if (!set.Any) { if (over) foreach (var x in draft.VoiceShifts) x.Lines = null; await UnshiftVoice(); return; }
        var entries = new Dictionary<string, VoiceShiftEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _) in targets)
        {
            var e = set.Settings();
            e.Package = file;
            var was = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase))?.Clone();
            e.Replaced = was?.Replaced;
            e.Lines = over ? null : was?.Lines;
            entries[file] = e;
        }
        await BuildVoice(entries, null, "Whole Voice: " + (ShapeText(set) is { Length: > 0 } t ? t : "As It Is"));
    }

    /// <summary>These packages' voices rendered (VoiceShiftBuild) into the work folder; Save writes them. A package with nothing
    /// left to do goes back to its original lines.</summary>
    async Task BuildVoice(Dictionary<string, VoiceShiftEntry> entries, IReadOnlyDictionary<string, byte[]>? newLines, string label)
    {
        if (shifting || game == null) return;
        var audioWas = AudioNow();
        shifting = audioBusy = true;
        UpdateAudioButtons();
        string cooked = game.Cooked;
        voiceWork ??= Path.Combine(lib.DataFolder, "voice-work-" + Guid.NewGuid().ToString("N")[..8]);
        var progress = new Progress<string>(m => { voiceShiftNote.ForeColor = Ui.Subtle; voiceShiftNote.Text = m; });
        var logs = new List<string>();
        try
        {
            foreach (var (file, entry) in entries)
            {
                if (!entry.Any && entry.Replaced is not { Count: > 0 } && entry.Lines is not { Count: > 0 }) { RemoveShift(file); continue; }
                if (entry.Replaced is { Count: 0 }) entry.Replaced = null;
                string source = draft.Packages.First(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Source;
                string? earlier = draft.SoundPacks.FirstOrDefault(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase));
                var packs = draft.SoundPacks.Where(x => !Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase)).ToList();
                var off = draft.VoiceOff.ToList();
                string work = Path.Combine(voiceWork, Guid.NewGuid().ToString("N")[..6]);
                var (pkg, pack, log) = await Task.Run(() => VoiceShiftBuild.Build(source, file, off, packs, cooked, entry, work, progress, newLines, earlier));
                if (IsDisposed) return;
                PutVoiceBuild(file, entry, pkg, pack);
                logs.AddRange(log.Select(l => $"{file}: {l}"));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or UnauthorizedAccessException)
        {
            shifting = audioBusy = false; ShowShiftNote(); UpdateAudioButtons();
            Dialog.Show(this, "The voice couldn't be shifted: " + ex.Message, "Voice Not Shifted", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        shifting = audioBusy = false;
        voiceWavs.Clear();
        RefreshPackages(); RefreshSounds(); RefreshVoice(); ShowShiftNote();
        AudioRecord(label, audioWas);
        UpdateAudioButtons();
        voiceShiftNote.Text += " · Not Saved Yet";
        // Sound effects in a voice set are always left out (that's by design); anything else left out is worth a look.
        if (logs.Any(l => l.Contains("left as it is") && !VoiceShiftBuild.ExpectedSkip(l))) Dialog.ShowLog(this, string.Join(Environment.NewLine, logs), "Voice Shifted");
    }

    /// <summary>A built voice into the draft.</summary>
    void PutVoiceBuild(string file, VoiceShiftEntry entry, string pkg, string pack)
    {
        int k = draft.Packages.FindIndex(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        draft.Packages[k] = (file, pkg);
        draft.SoundPacks.RemoveAll(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase));
        draft.SoundPacks.Add(pack);
        draft.VoiceShifts.RemoveAll(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase));
        draft.VoiceShifts.Add(entry);
        voiceBuilt[file] = System.Text.Json.JsonSerializer.Serialize(entry);
    }

    /// <summary>Before saving: packages whose lines' own settings changed since they were built. Only the changed lines are encoded
    /// (VoiceShiftBuild keeps the others), so this takes moments even for a whole voice.</summary>
    void BuildVoiceRows(List<string> log)
    {
        if (game == null) return;
        foreach (var entry in draft.VoiceShifts.ToList())
        {
            string file = entry.Package, now = System.Text.Json.JsonSerializer.Serialize(entry);
            string? built = voiceBuilt.GetValueOrDefault(file) ?? (editing?.Manifest.VoiceShifts?.FirstOrDefault(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase)) is { } saved ? System.Text.Json.JsonSerializer.Serialize(saved) : null);
            if (built == now) continue;
            if (!draft.Packages.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            if (!entry.Any && entry.Replaced is not { Count: > 0 } && entry.Lines is not { Count: > 0 }) { RemoveShift(file); continue; }
            string source = draft.Packages.First(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Source;
            string? earlier = draft.SoundPacks.FirstOrDefault(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase));
            var packs = draft.SoundPacks.Where(x => !Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase)).ToList();
            voiceWork ??= Path.Combine(lib.DataFolder, "voice-work-" + Guid.NewGuid().ToString("N")[..8]);
            var (pkg, pack, said) = VoiceShiftBuild.Build(source, file, draft.VoiceOff.ToList(), packs, game.Cooked, entry, Path.Combine(voiceWork, Guid.NewGuid().ToString("N")[..6]), null, null, earlier);
            PutVoiceBuild(file, entry, pkg, pack);
            log.Add($"{file}: {said[0]}");
        }
    }

    /// <summary>Remove Shift: the voice set back at the original lines, the shift's sound pack out (one package's, else all).</summary>
    void RemoveShift(string? only = null)
    {
        var audioWas = audioBusy ? null : AudioNow();
        bool any = false;
        foreach (var (file, source) in draft.Packages.ToList())
        {
            if (!File.Exists(source) || only != null && !only.Equals(file, StringComparison.OrdinalIgnoreCase)) continue;
            byte[]? undone;
            try { undone = VoiceShiftBuild.Unshift(source, file, draft.VoiceOff); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { Dialog.Show(this, ex.Message, "Shift Not Removed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (undone != null)
            {
                voiceWork ??= Path.Combine(lib.DataFolder, "voice-work-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(voiceWork);
                string path = Path.Combine(Directory.CreateDirectory(Path.Combine(voiceWork, Guid.NewGuid().ToString("N")[..6])).FullName, file);   // (its own name: see UseWorkCopy)
                File.WriteAllBytes(path, undone);
                int k = draft.Packages.FindIndex(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                draft.Packages[k] = (file, path);
                any = true;
            }
            if (draft.SoundPacks.RemoveAll(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase)) > 0) any = true;
        }
        any |= draft.VoiceShifts.Count > 0;
        draft.VoiceShifts.RemoveAll(x => only == null || only.Equals(x.Package, StringComparison.OrdinalIgnoreCase));
        foreach (string f in voiceBuilt.Keys.Where(f => only == null || only.Equals(f, StringComparison.OrdinalIgnoreCase)).ToList()) voiceBuilt.Remove(f);
        voiceWavs.Clear();
        RefreshPackages(); RefreshSounds(); RefreshVoice(); ShowShiftNote();
        if (audioWas != null) AudioRecord("Remove Shift", audioWas);
        if (any) voiceShiftNote.Text += " · Not Saved Yet";
    }

    async void PlayVoice(VoiceLine l)
    {
        int id = ++voicePlayId;
        string leaf = l.Event[(l.Event.LastIndexOf('.') + 1)..];
        voiceStatus.ForeColor = Ui.Subtle;
        voiceStatus.Text = "Loading " + leaf + "…";
        var packs = draft.SoundPacks.ToList();
        string cooked = game?.Cooked ?? "";
        try
        {
            // A shifted line plays as the mod has it when the sliders are the saved shift; otherwise the original line with
            // the sliders' settings (a preview).
            var set = VoiceSetSliders;
            var saved = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(l.Package, StringComparison.OrdinalIgnoreCase));
            // A line with settings of its own plays with them (built at Save).
            if (RowOf(l) is { } row)
            {
                string orig = VoiceLines.Leaf(l.Event);
                string? pack = draft.SoundPacks.FirstOrDefault(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(l.Package), StringComparison.OrdinalIgnoreCase));
                bool mine = IsOwnLine(l) && pack != null;
                string plain = VoiceShiftBuild.Original(l.Event);
                byte[] heard = await Task.Run(() => VoiceShift.Process(mine ? VoiceShiftBuild.Saved(orig, pack!) : VoiceAudio.ToWav(VoiceAudio.Wem(plain, packs, cooked).Wem), row));
                if (id != voicePlayId || IsDisposed) return;
                VoiceAudio.Play(heard);
                voiceStatus.Text = "Playing " + leaf + " (This Line's Settings)";
                return;
            }
            bool asIs = l.Event.EndsWith(VoiceShiftBuild.Suffix, StringComparison.OrdinalIgnoreCase) && saved != null && (Same(saved, set) || saved.IsReplaced(leaf.Replace(VoiceShiftBuild.Suffix, "")));
            string ev = asIs ? l.Event : VoiceShiftBuild.Original(l.Event);
            if (!voiceWavs.TryGetValue(ev, out byte[]? wav))
            {
                wav = await Task.Run(() => VoiceAudio.ToWav(VoiceAudio.Wem(ev, packs, cooked).Wem));
                voiceWavs[ev] = wav;
            }
            if (id != voicePlayId || IsDisposed) return;
            bool shiftNow = !asIs && set.Any;
            if (shiftNow)
            {
                byte[] src = wav;
                wav = await Task.Run(() => VoiceShift.Process(src, set));
                if (id != voicePlayId || IsDisposed) return;
            }
            VoiceAudio.Play(wav);
            voiceStatus.Text = "Playing " + leaf + (asIs ? " (As Saved)" : shiftNow ? $" (Preview: Pitch {set.Pitch:+0.#;-0.#;0}, Formant {set.Formant:+0.#;-0.#;0}, Warmth {set.Warmth:+0.#;-0.#;0} dB" + (VoiceEffects.Any(set) ? ", with Effects)" : ")") : "");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            if (id != voicePlayId || IsDisposed) return;
            voiceStatus.ForeColor = Ui.Packages;
            voiceStatus.Text = "No Sound: " + ex.Message + (ex.Message.Contains("isn't in the game's sound files") ? " (silent in the game too)" : "");
        }
    }

    bool IsOn(VoiceLine l) => voiceWanted.TryGetValue((l.Package, l.Offset), out bool on) ? on : !l.Off;

    void RefreshVoice()
    {
        voiceLines = [.. draft.Packages.SelectMany(p => File.Exists(p.Source) ? VoiceSet.Read(p.File, p.Source, draft.VoiceOff) : [])];
        FillVoice();
        UpdateVoiceHint();
    }

    void FillVoice()
    {
        if (voice == null) return;
        string[] words = voiceFind.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keep = PickedLines();
        voiceFilling = true;
        voice.Rows.Clear();
        voice.Columns["Package"]!.Visible = voiceLines.Select(l => l.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        // (every line, not only the ones found: the column doesn't jump as you type)
        voice.Columns["Sound Event"]!.Width = voiceLines.Select(l => TextRenderer.MeasureText(l.Event, voice.Font).Width).DefaultIfEmpty((int)(300 * S)).Max() + (int)(24 * S);
        foreach (var l in voiceLines)
        {
            string said = !l.Sound ? "" : IsOwnLine(l) ? "(Your Own Line)" : VoiceText.For(l.Event);
            string setting = LineSetting(l);
            string text = $"{l.Situation} {l.Detail} {setting} {said} {l.Event}";
            if (!words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            bool on = IsOn(l);
            int i = voice.Rows.Add(on, l.Sound ? "▶" : "", l.Situation, l.Missing ? (l.Detail.Length > 0 ? l.Detail + " · " : "") + "Not in This Voice" : l.Detail, setting, said, l.Event, l.Package);
            voice.Rows[i].Tag = l;
            PaintSetting(voice.Rows[i].Cells["Settings"], setting);
            voice.Rows[i].Cells["What's Said"].ToolTipText = said.Length > 0 ? said + Environment.NewLine + "(written down by speech recognition: close, not exact)" : "";
            voice.Rows[i].Cells["play"].ToolTipText = l.Missing ? "Play the hero's own line (the one turned off here)" : "Play this line";
            if (l.Missing)
            {
                voice.Rows[i].Cells["on"].ReadOnly = true;
                foreach (DataGridViewCell c in voice.Rows[i].Cells)
                    c.ToolTipText = "The moved voice has no line for this situation, so it's off: otherwise the hero's own line would play here. (It can't be turned on: that sound isn't in this mod.)";
            }
            if (!l.Sound)
                foreach (DataGridViewCell c in voice.Rows[i].Cells)
                    c.ToolTipText = "Not a sound: this entry names a character or animation the voice refers to (a banter target names who other heroes' banter is about). Nothing to play or shift here.";
            if (!on || !l.Sound) voice.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle;
        }
        voice.ClearSelection();
        foreach (DataGridViewRow row in voice.Rows) if (row.Tag is VoiceLine l && keep.Any(k => k.Package == l.Package && k.Offset == l.Offset)) row.Selected = true;
        voiceFilling = false;
        ShowVoiceShape();
        UpdateVoiceCount();
    }

    void UpdateVoiceCount() =>
        voiceCount.Text = voiceLines.Count == 0 ? "No Voice Set in This Mod's Packages" : !voiceLines.Any(l => l.Sound) ? "No Voice Lines in This Mod's Packages"
            : Ui.TitleCase($"{voiceLines.Count(l => l.Sound && IsOn(l))} of {voiceLines.Count(l => l.Sound)} Lines On");

    /// <summary>
    /// Before saving: each package whose lines changed is rewritten (VoiceSet.Write: the voice set's references, verified)
    /// into a work folder and used as the draft's source; the manifest's VoiceOff keeps what's off. Returns the work folder.
    /// </summary>
    string? ApplyVoice()
    {
        var changed = voiceLines.Where(l => voiceWanted.TryGetValue((l.Package, l.Offset), out bool on) && on == l.Off).ToList();
        if (changed.Count == 0) return null;
        string work = Path.Combine(lib.DataFolder, "voice-edit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        foreach (var g in changed.GroupBy(l => l.Package, StringComparer.OrdinalIgnoreCase))
        {
            int k = draft.Packages.FindIndex(p => p.File.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (k < 0) continue;
            byte[]? bytes = VoiceSet.Write(draft.Packages[k].Source, [.. g.Select(l => (l.Offset, l.Off ? l.Event : (string?)null))]);
            if (bytes == null) continue;
            string file = Path.Combine(work, g.Key);
            File.WriteAllBytes(file, bytes);
            draft.Packages[k] = (g.Key, file);
            foreach (var l in g)
            {
                draft.VoiceOff.RemoveAll(o => o.Package.Equals(l.Package, StringComparison.OrdinalIgnoreCase) && o.Offset == l.Offset);
                if (!l.Off) draft.VoiceOff.Add(new VoiceOffEntry { Package = l.Package, Offset = l.Offset, Event = l.Event });
            }
        }
        return work;
    }

    /// <summary>
    /// Before saving: each character package's spare voice components put right (VoiceSet.RepairSpares: a voice copied by
    /// 0.60.242 or older from an audio package kept another hero's archetype and crashed the game on load; two users,
    /// 2026-10-28). A repaired package goes into a work folder and the draft points at it. Returns that folder, or null.
    /// </summary>
    string? RepairVoices(List<string> log)
    {
        string? dir = null;
        for (int i = 0; i < draft.Packages.Count; i++)
        {
            var (file, source) = draft.Packages[i];
            if (!(file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase)) || !File.Exists(source)) continue;
            byte[] was = File.ReadAllBytes(source);
            var said = new List<string>();
            byte[] now = VoiceSet.RepairSpares(was, said);
            if (ReferenceEquals(now, was)) continue;
            dir ??= Path.Combine(lib.DataFolder, "voicefix-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string outFile = Path.Combine(dir, file);
            File.WriteAllBytes(outFile, now);
            draft.Packages[i] = (file, outFile);
            log.Add($"{file}:"); log.AddRange(said.Select(l => "  " + l));
        }
        return dir;
    }
}
