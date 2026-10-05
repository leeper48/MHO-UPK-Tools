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
        voice = Ui.Grid(S, false, ("Situation", 230), ("Detail", 260), ("Sound Event", 0), ("Package", 280));
        voice.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", Width = (int)(46 * S), SortMode = DataGridViewColumnSortMode.NotSortable });
        voice.Columns.Insert(1, new DataGridViewTextBoxColumn { Name = "play", HeaderText = "Play", Width = (int)(50 * S), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = Ui.Accent, SelectionForeColor = Ui.Accent } });
        voice.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && voice.Columns[e.ColumnIndex].Name == "play" && voice.Rows[e.RowIndex].Tag is VoiceLine pl) { if (pl.Sound) PlayVoice(pl); return; }
            if (e.RowIndex < 0 || voice.Columns[e.ColumnIndex].Name != "on" || voice.Rows[e.RowIndex].Tag is not VoiceLine l) return;
            if (l.Missing) return;   // the moved voice has no line here; the hero's own isn't in the package to turn on
            bool on = !IsOn(l);
            voiceWanted[(l.Package, l.Offset)] = on;
            voice.Rows[e.RowIndex].Cells["on"].Value = on;
            voice.Rows[e.RowIndex].DefaultCellStyle.ForeColor = on ? Ui.Text : Ui.Subtle;
            UpdateVoiceCount();
        };
        voiceFind.TextChanged += (_, _) => FillVoice();
        MhoPackageModifier.Gui.SearchBox.AddClear(voiceFind);
        Disposed += (_, _) => { VoiceAudio.Stop(); if (voiceWork != null) try { Directory.Delete(voiceWork, true); } catch (IOException) { } };
        Ui.Tip(voiceFind, "Show only lines whose situation, detail or sound event has these words.");
        var find = new Label { Text = "Find", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 4, 0) };
        var allOn = Ui.FlatButton("Turn All On", () =>
        {
            foreach (var l in voiceLines) voiceWanted[(l.Package, l.Offset)] = true;
            FillVoice();
        }, tip: "Turn every line of the voice set back on.");
        var stop = Ui.FlatButton("Stop", () => { VoiceAudio.Stop(); voiceStatus.Text = ""; }, tip: "Stop the line that's playing.");
        Button? another = null;
        another = Ui.FlatButton("Use Another Voice ▾", () => VoiceMenu(another!),
            tip: "Give the costume a whole voice from the game: its hero's own (so you can play and turn off its lines), another costume's voice such as Lady Deadpool or Spider-Gwen, or any hero's.");
        // Voice shift (Kurt, 2026-10-02): the sliders preview with ▶; Shift This Voice renders every line into the mod
        // (VoiceShiftBuild: a sound pack MHModManager 1.0.1 applies too); Remove Shift puts the original lines back.
        voicePitch = new LightSlider { Label = "Pitch", Min = -12, Max = 12, Step = 0.5f, Mark = 0, Format = v => v == 0 ? "0 st" : $"{v:+0.#;-0.#} st", Home = () => 0, Size = new Size((int)(230 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(10 * S), 0) };
        voiceFormant = new LightSlider { Label = "Formant", Min = -6, Max = 6, Step = 0.5f, Mark = 0, Format = v => v == 0 ? "0 st" : $"{v:+0.#;-0.#} st", Home = () => 0, Size = new Size((int)(230 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(10 * S), 0) };
        voiceWarmth = new LightSlider { Label = "Warmth", Min = -6, Max = 6, Step = 0.5f, Mark = 0, Format = v => v == 0 ? "0 dB" : $"{v:+0.#;-0.#} dB", Home = () => 0, Size = new Size((int)(230 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(10 * S), 0) };
        voicePitch.Value = 0; voiceFormant.Value = 0; voiceWarmth.Value = 0;
        Ui.Tip(voiceWarmth, "More (or less) of the low end, the voice's body. A raised voice can sound thin; a little warmth helps.");   // (a LightSlider starts at 1: it was made for the light level)
        Ui.Tip(voicePitch, "How many semitones higher or lower the voice is (double-click: back to 0). ▶ plays lines with these settings; Shift This Voice puts them into the mod.");
        Ui.Tip(voiceFormant, "Moves the voice's formants (the size of the throat you hear) by semitones, apart from the pitch. Lower sounds bigger, higher smaller.");
        Button Preset(string text, float pitch, float formant, float warmth, string tip) => Ui.FlatButton(text, () => { voicePitch.Value = pitch; voiceFormant.Value = formant; voiceWarmth.Value = warmth; }, tip);
        var shiftNow = Ui.AccentButton("Shift This Voice", ShiftVoice, "Shift every line of the costume's voice with these settings (about 30 seconds). Lines that are off stay off; sound effects in the voice set aren't changed. Save puts it into the mod: a sound pack MHModManager can install too.");
        var unshift = Ui.FlatButton("Remove Shift", RemoveShift, "Put the costume's original voice lines back (Save to keep it).");
        var shiftRow = Toolbar(new Label { Text = "VOICE SHIFT", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 8, 0) }, voicePitch, voiceFormant, voiceWarmth,
            Preset("Female → Male", -5, -2.5f, 0, "Pitch -5, formants -2.5: a starting point for a female voice to sound male."),
            Preset("Male → Female", 5, 2.5f, 2, "Pitch +5, formants +2.5, warmth +2 dB: a starting point for a male voice to sound female."),
            Preset("Original", 0, 0, 0, "Back to the line as it is."), shiftNow, unshift, voiceShiftNote);
        if (draft.VoiceShifts.FirstOrDefault() is { } saved) { voicePitch.Value = saved.Pitch; voiceFormant.Value = saved.Formant; voiceWarmth.Value = saved.Warmth; }
        ShowShiftNote();
        var bars = new Panel { Dock = DockStyle.Top, AutoSize = true };
        var mainRow = Toolbar(find, voiceFind, allOn, stop, another, voiceCount, voiceStatus);
        voiceHintButton = Ui.AccentButton("Use the Hero's Voice", UseHeroVoice, "Add the hero's default costume package to the mod (the game's copy) and put the voice the game plays for it into that package, so its lines can be played, turned off and shifted.");
        voiceHintRow = Toolbar(voiceHint, voiceHintButton);
        voiceHintRow.Visible = false;
        bars.Controls.Add(voiceHintRow); bars.Controls.Add(shiftRow); bars.Controls.Add(mainRow);   // (docked top: the last added is on top)
        return Page(voice, bars,
            "The costume's voice set: what the hero says in each situation. Click ▶ to hear a line. Untick a line to turn it off (for example a donor voice naming its own team); it's saved into the mod's package, and can be turned on again. A stock costume has no voice set of its own (it uses its hero's): Use Another Voice puts its hero's voice in, or another costume's or hero's.");
    }

    /// <summary>Packages given another voice in this edit: their source before (for Back to This Mod's Voice).</summary>
    readonly Dictionary<string, string> voiceBefore = new(StringComparer.OrdinalIgnoreCase);
    string? voiceWork;

    /// <summary>The draft's costume packages (UC__MarvelPlayer_&lt;Hero&gt;_&lt;Costume&gt;_SF): the ones a voice can be put into.</summary>
    List<(string File, string Source)> CostumePackages() =>
        [.. draft.Packages.Where(p => File.Exists(p.Source) && Regex.IsMatch(p.File, @"^UC__MarvelPlayer_[A-Za-z0-9]+_[A-Za-z0-9_]+_SF\.upk$", RegexOptions.IgnoreCase))];

    /// <summary>The voice menu: per costume package, its hero's voices first, then every other hero's (submenus by hero).</summary>
    async void VoiceMenu(Control button)
    {
        var targets = CostumePackages();
        if (targets.Count == 0) { Dialog.Show(this, "A voice goes into a costume package (UC__MarvelPlayer_<Hero>_<Costume>_SF.upk). Add one on the Packages tab first.", "No Costume Package", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (game == null) { Dialog.Show(this, "Set the game folder first (Settings → Change Game Folder): the voices come from the game's files.", "No Game Folder", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
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
            if (targets.Count > 1) { var sub = new ToolStripMenuItem(file); menu.Items.Add(sub); items = sub.DropDownItems; }
            string hero = (VoiceSet.HeroOf(file) ?? "").ToLowerInvariant();
            bool Mine(VoiceSet.Source v) => hero.Length > 0 && (v.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase) || v.Hero.StartsWith(hero, StringComparison.OrdinalIgnoreCase) || hero.StartsWith(v.Hero.ToLowerInvariant()));
            var own = all.Where(Mine).ToList();
            foreach (var v in own) items.Add(VoiceItem(file, v));
            if (own.Count > 0) items.Add(new ToolStripSeparator());
            var others = new ToolStripMenuItem("Other Heroes");
            foreach (var g in all.Where(v => !Mine(v)).GroupBy(v => v.Title.Split(" · ")[0]))
            {
                if (g.Count() == 1) { others.DropDownItems.Add(VoiceItem(file, g.First())); continue; }
                var h = new ToolStripMenuItem(g.Key);
                foreach (var v in g) h.DropDownItems.Add(VoiceItem(file, v));
                others.DropDownItems.Add(h);
            }
            items.Add(others);
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
    async void UseVoice(string file, VoiceSet.Source? v)
    {
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
            string path = Path.Combine(voiceWork, Guid.NewGuid().ToString("N")[..6] + "_" + file);
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
    }

    readonly Label voiceStatus = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(14, 8, 0, 0) };
    readonly Dictionary<string, byte[]> voiceWavs = new(StringComparer.OrdinalIgnoreCase);
    int voicePlayId;

    /// <summary>Plays a line (VoiceAudio: the mod's sound pack, else the game's sound files), found and decoded in the background.</summary>
    LightSlider? voicePitch, voiceFormant, voiceWarmth;
    readonly Label voiceHint = new() { AutoSize = true, ForeColor = Ui.OverrideAmber, Padding = new Padding(0, 8, 8, 0) };
    Button? voiceHintButton;
    FlowLayoutPanel? voiceHintRow;
    (string CostumeFile, string CostumeName, VoiceSet.Source Voice)? heroVoice;
    int hintRun;

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
        bool have = draft.Packages.Any(p => p.File.Equals(f.CostumeFile, StringComparison.OrdinalIgnoreCase));
        voiceHint.Text = $"No voice lines in this mod's packages. In the game, {heroName}'s {f.CostumeName} costume plays the voice \"{f.Voice.Title}\""
            + (have ? "." : $" ({f.CostumeFile}, not in this mod).");
        voiceHintButton!.Text = have ? "Use " + f.Voice.Title : $"Add {f.CostumeName} and Use Its Voice";
        voiceHintRow.Visible = true;
    }

    void UseHeroVoice()
    {
        if (heroVoice is not { } f || game == null) return;
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
        UseVoice(f.CostumeFile, f.Voice);
    }
    readonly Label voiceShiftNote = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 0, 0) };
    bool shifting;

    VoiceShiftEntry Sliders => new() { Pitch = voicePitch?.Value ?? 0, Formant = voiceFormant?.Value ?? 0, Warmth = voiceWarmth?.Value ?? 0 };
    static bool Same(VoiceShiftEntry a, VoiceShiftEntry b) => Math.Abs(a.Pitch - b.Pitch) < 0.01f && Math.Abs(a.Formant - b.Formant) < 0.01f && Math.Abs(a.Warmth - b.Warmth) < 0.01f;

    void ShowShiftNote()
    {
        var v = draft.VoiceShifts.FirstOrDefault();
        voiceShiftNote.ForeColor = v == null ? Ui.Subtle : Ui.Enabled;
        voiceShiftNote.Text = v == null ? "Not Shifted" : $"Shifted: Pitch {v.Pitch:+0.#;-0.#;0}, Formant {v.Formant:+0.#;-0.#;0}, Warmth {v.Warmth:+0.#;-0.#;0} dB";
    }

    /// <summary>Shift This Voice: every costume package's voice rendered (VoiceShiftBuild) into the work folder; Save writes it.</summary>
    void ShiftVoice() => _ = ShiftVoiceAsync();

    /// <summary>For --voice-shift-tab-test: the sliders set, then Shift This Voice (or Remove Shift).</summary>
    internal Task ShiftForTest(float pitch, float formant, float warmth)
    {
        voicePitch!.Value = pitch; voiceFormant!.Value = formant; voiceWarmth!.Value = warmth;
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

    async Task ShiftVoiceAsync()
    {
        if (shifting || game == null) return;
        var targets = draft.Packages.Where(p => File.Exists(p.Source) && VoiceSet.Read(p.File, p.Source, draft.VoiceOff).Any(l => l.Sound)).ToList();
        if (targets.Count == 0) { Dialog.Show(this, "No package of this mod has a voice set. Use Another Voice gives a costume one first.", "No Voice to Shift", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var set = Sliders;
        if (set.Pitch == 0 && set.Formant == 0 && set.Warmth == 0) { RemoveShift(); return; }
        shifting = true;
        string cooked = game.Cooked;
        voiceWork ??= Path.Combine(lib.DataFolder, "voice-work-" + Guid.NewGuid().ToString("N")[..8]);
        var progress = new Progress<string>(m => { voiceShiftNote.ForeColor = Ui.Subtle; voiceShiftNote.Text = m; });
        var logs = new List<string>();
        try
        {
            foreach (var (file, source) in targets)
            {
                var entry = new VoiceShiftEntry { Package = file, Pitch = set.Pitch, Formant = set.Formant, Warmth = set.Warmth };
                var packs = draft.SoundPacks.Where(x => !Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase)).ToList();
                var off = draft.VoiceOff.ToList();
                string work = Path.Combine(voiceWork, Guid.NewGuid().ToString("N")[..6]);
                var (pkg, pack, log) = await Task.Run(() => VoiceShiftBuild.Build(source, file, off, packs, cooked, entry, work, progress));
                if (IsDisposed) return;
                int k = draft.Packages.FindIndex(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                draft.Packages[k] = (file, pkg);
                draft.SoundPacks.RemoveAll(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase));
                draft.SoundPacks.Add(pack);
                draft.VoiceShifts.RemoveAll(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase));
                draft.VoiceShifts.Add(entry);
                logs.AddRange(log.Select(l => $"{file}: {l}"));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or UnauthorizedAccessException)
        {
            shifting = false; ShowShiftNote();
            Dialog.Show(this, "The voice couldn't be shifted: " + ex.Message, "Voice Not Shifted", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        shifting = false;
        voiceWavs.Clear();
        RefreshPackages(); RefreshSounds(); RefreshVoice(); ShowShiftNote();
        voiceShiftNote.Text += " · Not Saved Yet";
        // Sound effects in a voice set are always left out (that's by design); anything else left out is worth a look.
        if (logs.Any(l => l.Contains("left as it is") && !VoiceShiftBuild.ExpectedSkip(l))) Dialog.ShowLog(this, string.Join(Environment.NewLine, logs), "Voice Shifted");
    }

    /// <summary>Remove Shift: the voice set back at the original lines, the shift's sound pack out.</summary>
    void RemoveShift()
    {
        bool any = false;
        foreach (var (file, source) in draft.Packages.ToList())
        {
            if (!File.Exists(source)) continue;
            byte[]? undone;
            try { undone = VoiceShiftBuild.Unshift(source, file, draft.VoiceOff); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { Dialog.Show(this, ex.Message, "Shift Not Removed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (undone != null)
            {
                voiceWork ??= Path.Combine(lib.DataFolder, "voice-work-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(voiceWork);
                string path = Path.Combine(voiceWork, Guid.NewGuid().ToString("N")[..6] + "_" + file);
                File.WriteAllBytes(path, undone);
                int k = draft.Packages.FindIndex(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                draft.Packages[k] = (file, path);
                any = true;
            }
            if (draft.SoundPacks.RemoveAll(x => Path.GetFileName(x).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase)) > 0) any = true;
        }
        any |= draft.VoiceShifts.Count > 0;
        draft.VoiceShifts.Clear();
        voiceWavs.Clear();
        RefreshPackages(); RefreshSounds(); RefreshVoice(); ShowShiftNote();
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
            var set = Sliders;
            var saved = draft.VoiceShifts.FirstOrDefault(x => x.Package.Equals(l.Package, StringComparison.OrdinalIgnoreCase));
            bool asIs = l.Event.EndsWith(VoiceShiftBuild.Suffix, StringComparison.OrdinalIgnoreCase) && saved != null && Same(saved, set);
            string ev = asIs ? l.Event : VoiceShiftBuild.Original(l.Event);
            if (!voiceWavs.TryGetValue(ev, out byte[]? wav))
            {
                wav = await Task.Run(() => VoiceAudio.ToWav(VoiceAudio.Wem(ev, packs, cooked).Wem));
                voiceWavs[ev] = wav;
            }
            if (id != voicePlayId || IsDisposed) return;
            bool shiftNow = !asIs && (set.Pitch != 0 || set.Formant != 0 || set.Warmth != 0);
            if (shiftNow)
            {
                byte[] src = wav;
                wav = await Task.Run(() => VoiceShift.Shift(src, set.Pitch, set.Formant, VoiceShift.Mode.Natural, set.Warmth));
                if (id != voicePlayId || IsDisposed) return;
            }
            VoiceAudio.Play(wav);
            voiceStatus.Text = "Playing " + leaf + (asIs ? " (Shifted, as Saved)" : shiftNow ? $" (Preview: Pitch {set.Pitch:+0.#;-0.#;0}, Formant {set.Formant:+0.#;-0.#;0}, Warmth {set.Warmth:+0.#;-0.#;0} dB)" : "");
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
        voice.Rows.Clear();
        voice.Columns["Package"]!.Visible = voiceLines.Select(l => l.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
        foreach (var l in voiceLines)
        {
            string text = $"{l.Situation} {l.Detail} {l.Event}";
            if (!words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            bool on = IsOn(l);
            int i = voice.Rows.Add(on, l.Sound ? "▶" : "", l.Situation, l.Missing ? (l.Detail.Length > 0 ? l.Detail + " · " : "") + "Not in This Voice" : l.Detail, l.Event, l.Package);
            voice.Rows[i].Tag = l;
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
}
