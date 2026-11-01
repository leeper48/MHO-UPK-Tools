using System.Text.RegularExpressions;

namespace MhoExtendedModManager.Gui;

/// <summary>The Editor's Power Sounds tab: a hero's power sound effects (play, turn off, export, replace).</summary>
sealed partial class ModEditorView
{
    sealed record SoundRow(string Power, PowerSound Sound);

    DataGridView soundRows = null!;
    readonly DropDown soundHero = new() { Placeholder = "Hero" };
    readonly TextBox soundFind = new() { Width = 160 };
    readonly Label soundStatus = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(14, 8, 0, 0) };
    readonly Dictionary<string, List<SoundRow>> heroSounds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sounds replaced since the last save: event name → WAV in the game's format.</summary>
    readonly Dictionary<string, byte[]> soundWavs = new(StringComparer.OrdinalIgnoreCase);
    List<SoundRow> shownSounds = [];
    bool soundHeroesFilled, soundFilling;
    int soundRun, soundPlayId;
    readonly List<LightSlider> soundFilters = [];
    LightSlider soundVolume = null!;
    readonly List<Control> soundShapeControls = [];
    Button soundScopeSet = null!, soundScopeRows = null!, soundApplyAll = null!, soundRemoveSet = null!, soundBackToSet = null!;
    readonly Label soundSetNote = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 0, 0) };
    /// <summary>The sliders are the selected sounds' own settings, not the whole set's.</summary>
    bool soundRowScope;
    /// <summary>The whole set's sliders, while they show the selected sounds.</summary>
    PowerSoundSet soundSetKept = new();
    /// <summary>Event name → how many sounds it picks among, when more than one (a clone keeps one).</summary>
    readonly Dictionary<string, int> soundVariations = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> soundSwitches = new(StringComparer.OrdinalIgnoreCase);

    Control PowerSoundsPage()
    {
        soundRows = Ui.Grid(S, false, ("Power", 200), ("Part", 130), ("When", 140), ("Settings", 210), ("Note", 230), ("Sound Event", 300), ("Package", 0));
        soundRows.Columns["Package"]!.MinimumWidth = (int)(200 * S);
        soundRows.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", Width = (int)(46 * S), SortMode = DataGridViewColumnSortMode.NotSortable });
        soundRows.Columns.Insert(1, new DataGridViewTextBoxColumn { Name = "play", HeaderText = "Play", Width = (int)(50 * S), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = Ui.Accent, SelectionForeColor = Ui.Accent } });
        soundRows.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || soundRows.Rows[e.RowIndex].Tag is not SoundRow r) return;
            if (soundRows.Columns[e.ColumnIndex].Name == "play") PlaySound(r);
            else if (soundRows.Columns[e.ColumnIndex].Name == "on") ToggleSound(r);
        };
        soundRows.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || soundRows.Rows[e.RowIndex].Tag is not SoundRow) return;
            if (!soundRows.Rows[e.RowIndex].Selected) { soundRows.ClearSelection(); soundRows.Rows[e.RowIndex].Selected = true; }
            var picked = PickedSounds();
            var menu = new ContextMenuStrip();
            if (picked.Count == 1)
                menu.Items.Add(new ToolStripMenuItem("Replace This Sound", null, (_, _) => ReplaceSound(picked[0])) { Enabled = CanChange(picked[0]), ToolTipText = CanChange(picked[0]) ? "Pick an audio file to play instead of this sound." : "This row switches music or a filter: it has no sound to replace." });
            menu.Items.Add(new ToolStripMenuItem(picked.Count == 1 ? "Export This Sound" : $"Export {picked.Count} Sounds", null, (_, _) => ExportSounds(picked)) { ToolTipText = "Saves them as .WAV files." });
            if (picked.Any(r => SoundEntry(r.Sound)?.Replaced == true)) menu.Items.Add(new ToolStripMenuItem("Back to the Original Sound", null, (_, _) => OriginalSounds(picked)) { ToolTipText = "Takes your file out: the sound plays as it did before." });
            if (PickedShapable().Any(r => SoundEntry(r.Sound) is { InSet: false } e && (e.Shaped || SoundSetNow() != null))) menu.Items.Add(new ToolStripMenuItem("Back to the Set", null, (_, _) => SoundsBackToSet()) { ToolTipText = "Drops these sounds' own settings: they follow the whole set again, or play as they are when there's no set." });
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(soundRows, soundRows.PointToClient(Cursor.Position));
        };
        soundRows.SelectionChanged += (_, _) => { if (!soundFilling) ShowSoundShape(); };
        soundFind.TextChanged += (_, _) => FillSounds();
        MhoPackageModifier.Gui.SearchBox.AddClear(soundFind);
        Ui.Tip(soundFind, "Show only sounds whose power, part, settings, note, event or package has these words.");
        soundHero.Width = (int)(170 * S);
        soundHero.Format = o => AutoTags.DisplayName((string)o) ?? (string)o;
        soundHero.SelectedIndexChanged += (_, _) => { if (soundHero.SelectedItem is string h) { ShowSoundSet(); LoadSounds(h); } };
        Ui.Tip(soundHero, "The hero whose power sounds are listed. It opens on the mod's hero.");
        var find = new Label { Text = "Find", AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 4, 0) };
        var allOn = Ui.FlatButton("Turn All On", () => SetSoundOn(shownSounds, true), tip: "Turn every sound of this hero back on.");
        var stop = Ui.FlatButton("Stop", () => { VoiceAudio.Stop(); soundStatus.Text = ""; }, tip: "Stop the sound that's playing.");
        var export = Ui.FlatButton("Export Sounds", () => ExportSounds(soundRows.SelectedRows.Count > 1 ? PickedSounds() : shownSounds), "Saves the hero's power sounds as .WAV files named after their sound events, with lines.csv saying which power each belongs to. Selected rows: only those.");
        var replace = Ui.FlatButton("Replace Sounds", ReplaceSounds, $"Pick a folder of {VoiceLines.Formats} files named as Export Sounds names them: those sounds are replaced by your files. You don't need all of them. Any rate, mono or stereo. Looping sounds can't be replaced yet. Right-click a row for one sound.");
        var bar = Toolbar([new Label { Text = "Hero", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 4, 0) }, soundHero, find, soundFind, allOn, stop, export, replace, .. AudioButtons(), soundStatus]);
        var bars = new Panel { Dock = DockStyle.Top, AutoSize = true };
        var shape = SoundShapeRows();
        foreach (var row in shape.Reverse()) bars.Controls.Add(row);
        bars.Controls.Add(bar);   // (docked top: the last added is on top)
        SetSoundScope(false);
        var page = Page(soundRows, bars, "The sounds of a hero's powers, and of what those powers set off (projectiles, hits, conditions). Click ▶ to hear one, untick one to turn it off, or replace it with your own. Volume and effects go on all of them or on the selected ones. The power's package joins the mod on Save, so the change applies to every costume of that hero. Sounds that many heroes share (knockdowns, slows) aren't listed.");
        page.VisibleChanged += (_, _) => { if (page.Visible) FillSoundHeroes(); };
        return page;
    }

    /// <summary>The bars: what the sliders are for, then volume and the filters (the Voice tab's rows).</summary>
    FlowLayoutPanel[] SoundShapeRows()
    {
        LightSlider Slider(string label, float min, float max, float home, Func<float, string> format, string tip, Action<SoundShape, float> set)
        {
            var sl = new LightSlider { Label = label, Min = min, Max = max, Step = 0.05f, Mark = home, Format = format, Home = () => home, Size = new Size((int)(165 * S), (int)(30 * S)), Margin = new Padding(0, 2, (int)(8 * S), 0) };
            sl.Value = home;
            sl.Committed += () => SetSoundShape(e => set(e, sl.Value));
            Ui.Tip(sl, tip + " Press ▶ on a row to hear it (double-click: back).");
            return sl;
        }
        LightSlider Fx(string label, string tip, Action<SoundShape, float> set)
        {
            var sl = Slider(label, 0, 1, 0, v => v == 0 ? "Off" : $"{v * 100:0} %", tip, set);
            soundFilters.Add(sl);
            return sl;
        }
        soundVolume = Slider("Volume", 0.05f, 3, 1, v => $"{v * 100:0} %", "How loud the sound plays; 100 % is the sound as it is. Past full level the peaks are rounded off, so a sound that's already at full level won't get much louder.", (e, v) => e.Volume = Math.Abs(v - 1) < 0.001f ? null : v);
        var noFx = Ui.FlatButton("No Effects", () => { foreach (var sl in soundFilters) sl.Value = 0; SetSoundShape(e => e.Reverb = e.Echo = e.Distortion = e.LoFi = e.Chorus = e.Robot = 0); }, "Every filter off (the volume stays).");
        soundScopeSet = Ui.FlatButton("All Sounds", () => SetSoundScope(false), "The sliders are every sound's settings. Nothing changes until Apply to All Sounds. Rows with settings of their own keep them, unless you choose to override them. Looping sounds can't change.");
        soundScopeRows = Ui.FlatButton("Selected Sounds", () => SetSoundScope(true), "The sliders are the selected sounds' own settings, set as you move them. A row's own settings take the place of the whole set's. The Settings column says which each sound follows.");
        soundApplyAll = Ui.AccentButton("Apply to All Sounds", ApplySoundSet, "Gives every sound of this hero these settings, your own audio too. Save builds them.");
        soundRemoveSet = Ui.FlatButton("Remove Set", RemoveSoundSet, "Takes the whole set's settings off. Rows with settings of their own keep them.");
        soundBackToSet = Ui.FlatButton("Back to the Set", SoundsBackToSet, "Drops the selected sounds' own settings: they follow the whole set again, or play as they are when there's no set.");
        var scope = Toolbar(new Label { Text = "SETTINGS FOR", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 8, 0) }, soundScopeSet, soundScopeRows, soundApplyAll, soundRemoveSet, soundBackToSet, soundSetNote);
        // two rows (a wrapping row doesn't grow in a docked panel: the last slider was hidden)
        var row1 = Toolbar(new Label { Text = "EFFECTS", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 8, 0) }, soundVolume, noFx,
            Fx("Reverb", "A room around the sound: small at low settings, a hall at 100 %.", (e, v) => e.Reverb = v),
            Fx("Echo", "Repeats every quarter second, fading out (more repeats and louder as it goes up).", (e, v) => e.Echo = v));
        var row2 = Toolbar(
            Fx("Distortion", "Drives the sound into soft clipping: gritty to heavily distorted.", (e, v) => e.Distortion = v),
            Fx("Lo-Fi", "Radio or old-speaker sound: a narrow band, fewer bits and a lower sample rate as it goes up.", (e, v) => e.LoFi = v),
            Fx("Chorus", "Two slightly delayed, wavering copies of the sound: doubled, otherworldly.", (e, v) => e.Chorus = v),
            Fx("Robot", "Ring modulation: a metallic, robotic sound.", (e, v) => e.Robot = v));
        soundShapeControls.AddRange([soundVolume, noFx, .. soundFilters]);
        foreach (var row in new[] { scope, row1, row2 }) row.Padding = new Padding(0, 1, 0, 1);   // (room for the table)
        return [scope, row1, row2];
    }

    /// <summary>The hero's whole set, or null.</summary>
    PowerSoundSet? SoundSetNow() => soundHero.SelectedItem is string h ? draft.PowerSoundSets.FirstOrDefault(x => x.Hero.Equals(h, StringComparison.OrdinalIgnoreCase)) : null;

    PowerSoundSet SoundSliders()
    {
        var s = new PowerSoundSet { Volume = Math.Abs(soundVolume.Value - 1) < 0.001f ? null : soundVolume.Value };
        float[] f = [.. soundFilters.Select(x => x.Value)];
        s.Reverb = f[0]; s.Echo = f[1]; s.Distortion = f[2]; s.LoFi = f[3]; s.Chorus = f[4]; s.Robot = f[5];
        return s;
    }

    void ShowSoundSliders(SoundShape e)
    {
        soundVolume.Value = e.Volume ?? 1;
        foreach (var (sl, v) in soundFilters.Zip(new[] { e.Reverb, e.Echo, e.Distortion, e.LoFi, e.Chorus, e.Robot })) sl.Value = v;
    }

    /// <summary>All Sounds or Selected Sounds: what the sliders are.</summary>
    void SetSoundScope(bool rows)
    {
        if (rows != soundRowScope)
        {
            if (rows) soundSetKept = SoundSliders(); else ShowSoundSliders(soundSetKept);
            soundRowScope = rows;
        }
        Ui.Lit(soundScopeSet, !rows); Ui.Lit(soundScopeRows, rows);
        soundApplyAll.Visible = soundRemoveSet.Visible = !rows;
        soundBackToSet.Visible = rows;
        ShowSoundShape();
    }

    /// <summary>The hero's set into the sliders (another hero, Undo) and its note.</summary>
    void ShowSoundSet()
    {
        if (soundRows == null) return;
        var set = SoundSetNow();
        soundSetKept = set ?? new PowerSoundSet();
        if (!soundRowScope) ShowSoundSliders(soundSetKept);
        ShowSoundShape();
    }

    /// <summary>Is the row a sound (not a music or filter switch, which some loops are)?</summary>
    bool CanChange(SoundRow r) => !soundSwitches.Contains(PowerSounds.Leaf(r.Sound.Event));

    /// <summary>The selected rows the sliders work on.</summary>
    List<SoundRow> PickedShapable() => [.. PickedSounds().Where(CanChange)];

    /// <summary>Selected Sounds: the sliders show the first selected sound; with none selected they're off.</summary>
    void ShowSoundShape()
    {
        var picked = PickedShapable();
        if (soundRowScope) ShowSoundSliders((picked.Count > 0 ? SoundEntry(picked[0].Sound) : null) ?? (SoundShape)new PowerSoundSet());
        foreach (var c in soundShapeControls) c.Enabled = !soundRowScope || picked.Count > 0;
        soundBackToSet.Enabled = picked.Any(r => SoundEntry(r.Sound) is { InSet: false } e && (e.Shaped || SoundSetNow() != null));
        soundRemoveSet.Enabled = SoundSetNow() != null;
        var set = SoundSetNow();
        int own = shownSounds.Count(r => SoundEntry(r.Sound) is { InSet: false } e && (e.Shaped || set != null));
        soundSetNote.ForeColor = set != null || own > 0 ? Ui.Enabled : Ui.Subtle;
        soundSetNote.Text = (set == null ? "No Whole-Set Settings" : "All Sounds: " + (ShapeText(set.Filters) is { Length: > 0 } t ? t : "As They Are")) + (own > 0 ? $" · {own} With Own Settings" : "");
    }

    /// <summary>Every entry of these event names (rows sharing audio share settings), made when a row has none.</summary>
    List<PowerSoundEntry> EntriesOf(HashSet<string> leaves)
    {
        foreach (var r in shownSounds.Where(r => CanChange(r) && leaves.Contains(PowerSounds.Leaf(r.Sound.Event)) && SoundEntry(r.Sound) == null))
            draft.PowerSounds.Add(new PowerSoundEntry { Package = r.Sound.Package, Component = r.Sound.Component, Index = r.Sound.Index, Event = r.Sound.Event });
        return [.. draft.PowerSounds.Where(e => leaves.Contains(PowerSounds.Leaf(e.Event)))];
    }

    /// <summary>A slider moved in Selected Sounds: every selected sound's own settings, and each row sharing its audio (a step of
    /// the Audio tab's Undo). A sound that was in the set starts from the set's settings.</summary>
    void SetSoundShape(Action<SoundShape> set)
    {
        if (!soundRowScope) return;
        var own = PickedShapable();
        if (own.Count == 0) return;
        var was = AudioNow();
        var leaves = own.Select(r => PowerSounds.Leaf(r.Sound.Event)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in EntriesOf(leaves)) { e.InSet = false; set(e); }
        DropPlainSounds();
        AudioRecord(own.Count == 1 ? $"This Sound: {own[0].Power}, {Pretty(own[0].Sound.Part)}" : $"Selected Sounds: {own.Count}", was);
        ShowSoundRows();
        soundStatus.ForeColor = Ui.Subtle; soundStatus.Text = "Not Saved Yet";
        ShowSoundShape();
    }

    /// <summary>Entries that change nothing any more go (with a set in place one stays: it says the row is left out of the set).</summary>
    void DropPlainSounds()
    {
        if (SoundSetNow() == null) draft.PowerSounds.RemoveAll(e => !e.Off && !e.Replaced && !e.Shaped && !e.InSet && shownSounds.Any(r => SameEntry(e, r.Sound)));
    }

    static bool SameEntry(PowerSoundEntry e, PowerSound s) => e.Package.Equals(s.Package, StringComparison.OrdinalIgnoreCase) && e.Component.Equals(s.Component, StringComparison.OrdinalIgnoreCase) && e.Index == s.Index;

    /// <summary>Apply to All Sounds: the sliders become the hero's set, on every sound that can change.</summary>
    void ApplySoundSet()
    {
        var now = SoundSliders();
        if (!now.Shaped) { RemoveSoundSet(); return; }
        var rows = shownSounds.Where(CanChange).ToList();
        var set = SoundSetNow();
        var ownRows = rows.Where(r => SoundEntry(r.Sound) is { InSet: false } e && (e.Shaped || set != null)).ToList();
        var own = ownRows.Select(r => SoundEntry(r.Sound)!).ToHashSet();
        bool over = false;
        if (ownRows.Count > 0)
        {
            int pick = Dialog.Choose(this, $"{ownRows.Count} sound{(ownRows.Count == 1 ? " has" : "s have")} settings of {(ownRows.Count == 1 ? "its" : "their")} own. Should the whole set replace them?", "Sounds With Their Own Settings", "Keep Their Settings", "Override Them", "Cancel");
            if (pick == 2) return;
            over = pick == 1;
        }
        var was = AudioNow();
        if (set == null) draft.PowerSoundSets.Add(set = new PowerSoundSet { Hero = (string)soundHero.SelectedItem! });
        set.CopyShape(now);
        foreach (var e in EntriesOf(rows.Select(r => PowerSounds.Leaf(r.Sound.Event)).ToHashSet(StringComparer.OrdinalIgnoreCase)))
            if (over || !own.Contains(e)) { e.InSet = true; e.CopyShape(now); }
        AudioRecord("All Sounds: " + ShapeText(now.Filters), was);
        FillSounds();
        soundStatus.ForeColor = Ui.Subtle; soundStatus.Text = "Not Saved Yet";
    }

    void RemoveSoundSet()
    {
        var was = AudioNow();
        if (SoundSetNow() is { } set) draft.PowerSoundSets.Remove(set);
        foreach (var e in draft.PowerSounds.Where(e => shownSounds.Any(r => SameEntry(e, r.Sound))).Where(e => e.InSet || !e.Shaped)) { e.InSet = false; e.ClearShape(); }
        DropPlainSounds();
        AudioRecord("Remove Set", was);
        ShowSoundSet();
        FillSounds();
    }

    /// <summary>The selected sounds follow the set again (no set: they play as they are).</summary>
    void SoundsBackToSet()
    {
        var picked = PickedShapable();
        if (picked.Count == 0) return;
        var was = AudioNow();
        var set = SoundSetNow();
        foreach (var e in EntriesOf(picked.Select(r => PowerSounds.Leaf(r.Sound.Event)).ToHashSet(StringComparer.OrdinalIgnoreCase)))
        {
            e.ClearShape(); e.InSet = set != null;
            if (set != null) e.CopyShape(set);
        }
        DropPlainSounds();
        AudioRecord("Back to the Set", was);
        ShowSoundRows();
        ShowSoundShape();
    }

    void ShowSoundRows()
    {
        foreach (DataGridViewRow row in soundRows.Rows)
            if (row.Tag is SoundRow r) { row.Cells["Note"].Value = SoundNote(r); PaintSetting(row.Cells["Settings"], SoundSetting(r)); }
    }

    /// <summary>A row's Settings cell: what it follows (the column that tells a whole set's change from a row's own).</summary>
    string SoundSetting(SoundRow r)
    {
        if (!CanChange(r) || SoundEntry(r.Sound) is not { } e) return "";
        if (e.InSet) return "Whole Set";
        if (e.Shaped) return "This Sound: " + ShapeText(e.Filters);
        return SoundSetNow() != null ? "This Sound: As It Is" : "";
    }

    /// <summary>A row's Note: your own audio, that it loops, that a changed sound keeps one of its variations; where else it plays.</summary>
    string SoundNote(SoundRow r)
    {
        var e = SoundEntry(r.Sound);
        string own = !CanChange(r) ? "Music or Filter Switch" : e?.Replaced == true ? "Your Own Sound" : r.Sound.Stop != null ? "Loops" : "";
        int ways = soundVariations.GetValueOrDefault(PowerSounds.Leaf(r.Sound.Event));
        string kinds = ways < 2 ? "" : e?.Replaced == true || e?.Shaped == true ? $"Only 1 of Its {ways} Variations Plays" : $"{ways} Variations";
        return string.Join(" · ", new[] { own, kinds, AlsoIn(r) }.Where(x => x.Length > 0));
    }

    /// <summary>The hero list once: every hero, the mod's first.</summary>
    void FillSoundHeroes()
    {
        if (soundHeroesFilled || game == null) return;
        soundHeroesFilled = true;
        var heroes = Directory.EnumerateFiles(game.Cooked, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Select(n => Regex.Match(n, @"^UC__MarvelPlayer_([A-Za-z0-9]+)_SF$", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => m.Groups[1].Value)
            .OrderBy(h => AutoTags.DisplayName(h) ?? h, StringComparer.OrdinalIgnoreCase).ToList();
        soundHero.Items.AddRange(heroes);
        string? mine = draft.Packages.Select(p => HeroOf.Package(p.File, game.Cooked)).FirstOrDefault(h => h != null);
        int at = mine == null ? -1 : heroes.FindIndex(h => h.Equals(mine, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) soundHero.SelectedIndex = at;
    }

    /// <summary>The hero's powers and their sounds, in the background (once per hero).</summary>
    async void LoadSounds(string hero)
    {
        if (game == null) return;
        int run = ++soundRun;
        if (!heroSounds.ContainsKey(hero))
        {
            soundStatus.ForeColor = Ui.Subtle; soundStatus.Text = "Reading Powers…";
            var g = game;
            try
            {
                heroSounds[hero] = await Task.Run(() =>
                {
                    powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var rows = new List<SoundRow>();
                    foreach (var p in Fx.PowerList.For(powerDb, hero, g.Cooked, [], effectOnly: true))
                        foreach (string f in PowerRecolor.PackagesOf(powerDb, p.Prototype, hero, g.Cooked).Where(seen.Add))
                            if (StockFiles.For(g.Cooked, f) is var path && File.Exists(path))
                                rows.AddRange(PowerSounds.Read(f, path).Select(s => new SoundRow(p.Name, s)));
                    foreach (string ev in rows.Select(r => r.Sound.Event).Distinct(StringComparer.OrdinalIgnoreCase))
                        try
                        {
                            if (VoiceAudio.Variations(ev, g.Cooked).Sounds is > 1 and var n) lock (soundVariations) soundVariations[PowerSounds.Leaf(ev)] = n;
                            if (VoiceAudio.Actions(ev, g.Cooked) is { Count: > 0 } acts && !acts.Any(a => a.Type == 0x0403)) lock (soundSwitches) soundSwitches.Add(PowerSounds.Leaf(ev));
                        }
                        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException) { }
                    return rows;
                });
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException or ArgumentException or IndexOutOfRangeException)
            {
                soundStatus.ForeColor = Ui.Packages; soundStatus.Text = "Its Powers Couldn't Be Read: " + ex.Message;
                return;
            }
        }
        if (IsDisposed || run != soundRun) return;
        soundStatus.Text = "";
        FillSounds();
    }

    PowerSoundEntry? SoundEntry(PowerSound s) => draft.PowerSounds.FirstOrDefault(e => e.Package.Equals(s.Package, StringComparison.OrdinalIgnoreCase) && e.Component.Equals(s.Component, StringComparison.OrdinalIgnoreCase) && e.Index == s.Index);
    bool SoundOff(PowerSound s) => SoundEntry(s)?.Off == true;

    static string Pretty(string name)
    {
        string t = Regex.Replace(name, "sfx|sound", " ", RegexOptions.IgnoreCase).Trim().Replace('_', ' ');
        t = Regex.Replace(t, "(?<=[a-z])(hit|sweetener|start|end|loop|impact|cast|apply)", " $1", RegexOptions.IgnoreCase);   // (targethitsfx, impactsweetenersfx)
        return Ui.TitleCase(t.Length == 0 ? name : t);
    }

    void FillSounds()
    {
        if (soundRows == null) return;
        var keep = PickedSounds();
        shownSounds = soundHero.SelectedItem is string h ? heroSounds.GetValueOrDefault(h) ?? [] : [];
        string[] words = soundFind.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        soundFilling = true;
        soundRows.Rows.Clear();
        soundRows.Columns["Sound Event"]!.Width = shownSounds.Select(r => TextRenderer.MeasureText(r.Sound.Event, soundRows.Font).Width).DefaultIfEmpty((int)(300 * S)).Max() + (int)(24 * S);
        foreach (var r in shownSounds)
        {
            var s = r.Sound;
            string part = Pretty(s.Part), when = s.When.Length > 0 ? Ui.TitleCase(s.When.Replace("power_on_", "").Replace("projectile_on_", "").Replace('_', ' ')) : "";
            var e = SoundEntry(s);
            string note = SoundNote(r), setting = SoundSetting(r);
            if (!words.All(w => $"{r.Power} {part} {when} {setting} {note} {s.Event} {s.Package}".Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            int i = soundRows.Rows.Add(e?.Off != true, "▶", r.Power, part, when, setting, note, s.Event, s.Package);
            soundRows.Rows[i].Tag = r;
            PaintSetting(soundRows.Rows[i].Cells["Settings"], setting);
            soundRows.Rows[i].Cells["play"].ToolTipText = "Play this sound";
            soundRows.Rows[i].DefaultCellStyle.ForeColor = e?.Off == true ? Ui.Subtle : Ui.Text;
        }
        if (keep.Count > 0)
        {
            soundRows.ClearSelection();
            foreach (DataGridViewRow row in soundRows.Rows) if (row.Tag is SoundRow r && keep.Contains(r)) row.Selected = true;
        }
        soundFilling = false;
        ShowSoundShape();
    }

    /// <summary>The other rows that play the same event (Kurt, 2026-10-31: Taskmaster's .45 Caliber and Dual Wield fire the same
    /// gunshot from two components: turning one off left the gun firing).</summary>
    List<SoundRow> SameSound(SoundRow r) => [.. shownSounds.Where(x => !ReferenceEquals(x, r) && x.Sound.Event.Equals(r.Sound.Event, StringComparison.OrdinalIgnoreCase))];

    string AlsoIn(SoundRow r)
    {
        var same = SameSound(r);
        if (same.Count == 0) return "";
        var powers = same.Select(x => x.Power).Where(p => p != r.Power).Distinct().ToList();
        return powers.Count == 0 ? $"Used {same.Count + 1} Times" : powers.Count == 1 ? "Also in " + powers[0] : $"Also in {powers.Count} Powers";
    }

    /// <summary>A click on a row's tick: when the same sound plays elsewhere, it asks which.</summary>
    void ToggleSound(SoundRow r)
    {
        bool on = SoundOff(r.Sound);
        var others = SameSound(r).Where(x => SoundOff(x.Sound) == !on).ToList();
        if (others.Count == 0) { SetSoundOn([r], on); return; }
        string where = string.Join(Environment.NewLine, others.Select(x => $"  • {x.Power}, {Pretty(x.Sound.Part)}"));
        int pick = Dialog.Choose(this, $"This sound also plays in:{Environment.NewLine}{where}{Environment.NewLine}{Environment.NewLine}Turn it {(on ? "on" : "off")} in those too?", "Same Sound Elsewhere", $"All {others.Count + 1}", "Only This One", "Cancel");
        if (pick == 2) return;
        SetSoundOn(pick == 0 ? [r, .. others] : [r], on);
    }

    List<SoundRow> PickedSounds() => [.. soundRows.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => r.Tag).OfType<SoundRow>()];

    /// <summary>Sounds turned on or off (a step of the Audio tab's Undo).</summary>
    void SetSoundOn(IEnumerable<SoundRow> rows, bool on)
    {
        var was = AudioNow();
        foreach (var r in rows)
        {
            var e = SoundEntry(r.Sound);
            if (on && e != null) { e.Off = false; if (!e.Replaced && !e.Shaped && !e.InSet) draft.PowerSounds.Remove(e); }
            else if (!on)
            {
                if (e == null)
                {
                    draft.PowerSounds.Add(e = new PowerSoundEntry { Package = r.Sound.Package, Component = r.Sound.Component, Index = r.Sound.Index, Event = r.Sound.Event });
                    if (CanChange(r) && SoundSetNow() is { } set) { e.InSet = true; e.CopyShape(set); }
                }
                e.Off = true;
            }
        }
        var list = rows.ToList();
        AudioRecord(list.Count == 1 ? $"Sound {(on ? "On" : "Off")}: {list[0].Power}, {Pretty(list[0].Sound.Part)}" : $"{list.Count} Sounds {(on ? "On" : "Off")}", was);
        FillSounds();
    }

    async void PlaySound(SoundRow r)
    {
        int id = ++soundPlayId;
        string leaf = PowerSounds.Leaf(r.Sound.Event), cooked = game?.Cooked ?? "";
        soundStatus.ForeColor = Ui.Subtle; soundStatus.Text = "Loading " + leaf + "…";
        try
        {
            var packs = draft.SoundPacks.ToList();
            var own = SoundEntry(r.Sound);
            // With All Sounds up, a sound that follows the set plays with the sliders (a preview); one of its own, as it's set.
            bool follows = own == null || own.InSet || !own.Shaped && SoundSetNow() == null;
            SoundShape? shape = !CanChange(r) ? null : !soundRowScope && follows ? SoundSliders() : own;
            soundWavs.TryGetValue(leaf, out var mine);
            bool replaced = own?.Replaced == true;
            byte[] wav = await Task.Run(() =>
            {
                byte[] source = replaced ? mine ?? PowerSounds.Saved(leaf, packs) : VoiceAudio.ToWav(VoiceAudio.Wem(r.Sound.Event, packs, cooked).Wem);
                return shape?.Shaped == true ? PowerSounds.Render(source, shape) : source;
            });
            if (id != soundPlayId || IsDisposed) return;
            VoiceAudio.Play(wav);
            soundStatus.Text = "Playing " + leaf + (r.Sound.Stop != null ? " (Once: It Loops in Game)" : "");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            if (id != soundPlayId || IsDisposed) return;
            soundStatus.ForeColor = Ui.Packages; soundStatus.Text = "No Sound: " + ex.Message;
        }
    }

    /// <summary>The sounds as lines for VoiceLines (file names, lines.csv).</summary>
    static IEnumerable<VoiceLine> AsLines(IEnumerable<SoundRow> rows) => rows.Select(r => new VoiceLine(r.Sound.Package, r.Sound.Index, r.Power, Pretty(r.Sound.Part), r.Sound.Event, false));

    async void ExportSounds(List<SoundRow> rows)
    {
        if (game == null || rows.Count == 0) return;
        using var d = new FolderBrowserDialog { Description = "Folder for the sounds (.WAV files and lines.csv)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string folder = d.SelectedPath, cooked = game.Cooked;
        var lines = AsLines(rows).ToList();
        var packs = draft.SoundPacks.ToList();
        var progress = new Progress<string>(m => { soundStatus.ForeColor = Ui.Subtle; soundStatus.Text = m; });
        var (written, failed) = await Task.Run(() => VoiceLines.Export(lines, packs, cooked, folder, progress));
        if (IsDisposed) return;
        soundStatus.ForeColor = Ui.Subtle;
        soundStatus.Text = $"{written} Sound{(written == 1 ? "" : "s")} Exported";
        if (failed.Count > 0) Dialog.ShowLog(this, $"{written} exported. These have no sound:" + Environment.NewLine + string.Join(Environment.NewLine, failed), "Sounds Exported");
    }

    void ReplaceSounds()
    {
        using var d = new FolderBrowserDialog { Description = "Folder with your audio files, named as Export Sounds names them", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var (matched, unknown) = VoiceLines.Match(d.SelectedPath, shownSounds.Select(r => PowerSounds.Leaf(r.Sound.Event)));
        var loops = matched.Keys.Where(soundSwitches.Contains).ToList();
        foreach (string l in loops) matched.Remove(l);
        if (matched.Count == 0) { Dialog.Show(this, "No file there is named after a sound of this hero that can be replaced. Export Sounds shows the names.", "Nothing to Replace", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var skipped = unknown.Concat(loops.Select(l => l + " (music or filter switch)")).ToList();
        if (skipped.Count > 0 && Dialog.Show(this, $"{matched.Count} sound(s) will be replaced.{Environment.NewLine}{Environment.NewLine}{skipped.Count} file(s) are left out:{Environment.NewLine}{string.Join(Environment.NewLine, skipped.Take(12).Select(u => "  • " + u))}{(skipped.Count > 12 ? Environment.NewLine + "  …" : "")}",
                "Replace Sounds", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _ = PutSounds(matched, $"Replace {matched.Count} Sound{(matched.Count == 1 ? "" : "s")}");
    }

    void ReplaceSound(SoundRow r)
    {
        string leaf = PowerSounds.Leaf(r.Sound.Event);
        using var d = new OpenFileDialog { Title = "A sound to play instead of " + leaf, Filter = VoiceLines.FileFilter };
        if (d.ShowDialog(this) == DialogResult.OK) _ = PutSounds(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [leaf] = d.FileName }, $"Replace Sound: {r.Power}, {Pretty(r.Sound.Part)}");
    }

    /// <summary>The files into the draft: every row of this hero with that event (a sound can be in several packages). Save builds it.</summary>
    internal async Task PutSounds(IReadOnlyDictionary<string, string> files, string label)
    {
        var bad = new List<string>();
        var wavs = await Task.Run(() =>
        {
            var w = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (leaf, file) in files)
                try { w[leaf] = VoiceLines.Load(file); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or IndexOutOfRangeException or ArgumentException or UnauthorizedAccessException) { bad.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
            return w;
        });
        if (IsDisposed) return;
        if (wavs.Count > 0)
        {
            var was = AudioNow();
            foreach (var (k, v) in wavs) soundWavs[k] = v;
            foreach (var r in shownSounds.Where(r => wavs.ContainsKey(PowerSounds.Leaf(r.Sound.Event))))
            {
                var e = SoundEntry(r.Sound);
                if (e == null)
                {
                    draft.PowerSounds.Add(e = new PowerSoundEntry { Package = r.Sound.Package, Component = r.Sound.Component, Index = r.Sound.Index, Event = r.Sound.Event });
                    if (SoundSetNow() is { } set) { e.InSet = true; e.CopyShape(set); }
                }
                e.Replaced = true;
            }
            AudioRecord(label, was);
            FillSounds();
            soundStatus.ForeColor = Ui.Subtle; soundStatus.Text = "Not Saved Yet";
        }
        if (bad.Count > 0) Dialog.ShowLog(this, "These files couldn't be read:" + Environment.NewLine + string.Join(Environment.NewLine, bad), "Some Sounds Not Replaced");
    }

    void OriginalSounds(List<SoundRow> rows)
    {
        var was = AudioNow();
        var leaves = rows.Select(r => PowerSounds.Leaf(r.Sound.Event)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in draft.PowerSounds.Where(e => e.Replaced && leaves.Contains(PowerSounds.Leaf(e.Event))).ToList())
        {
            e.Replaced = false;
            if (!e.InSet) e.ClearShape();
            if (!e.Off && !e.InSet) draft.PowerSounds.Remove(e);
        }
        AudioRecord(rows.Count == 1 ? $"Back to the Original: {rows[0].Power}, {Pretty(rows[0].Sound.Part)}" : $"Back to the Original: {rows.Count} Sounds", was);
        FillSounds();
    }

    /// <summary>--audio-snapshot: a whole set with two sounds of their own.</summary>
    internal async Task SnapSounds()
    {
        FillSoundHeroes();
        for (int i = 0; i < 300 && soundRows.Rows.Count == 0; i++) await Task.Delay(100);
        SetSoundScope(false);
        soundVolume.Value = 0.8f; soundFilters[0].Value = 0.2f;
        ApplySoundSet();
        SetSoundScope(true);
        soundRows.ClearSelection();
        foreach (DataGridViewRow row in soundRows.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is SoundRow { Sound.Stop: null }).Skip(1).Take(2)) row.Selected = true;
        SetSoundShape(s => { s.Volume = 0.6f; s.Robot = 0.5f; });
        ShowSoundShape();
    }

    /// <summary>Test (--sound-shape-test): the sliders on a replaced sound through the tab; nothing is saved.</summary>
    internal async Task<int> TestSoundShape(string tone, Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        FillSoundHeroes();
        for (int i = 0; i < 300 && soundRows.Rows.Count == 0; i++) await Task.Delay(100);
        if (soundRows.Rows.Count < 2) { Check(false, "the hero has sounds"); return fails; }
        void Pick(params SoundRow[] rows)
        {
            soundRows.ClearSelection();
            foreach (DataGridViewRow row in soundRows.Rows) if (row.Tag is SoundRow r && rows.Contains(r)) row.Selected = true;
        }
        var ones = shownSounds.Where(r => r.Sound.Stop == null).DistinctBy(r => r.Sound.Event).Where(r => SameSound(r).Count == 0).ToList();
        SoundRow a = ones[0], b = ones[1], plain = ones[2], other = ones[3];
        string Cell(SoundRow r) => soundRows.Rows.Cast<DataGridViewRow>().First(x => x.Tag == r).Cells["Settings"].Value as string ?? "";
        // one sound of the game's, by itself
        SetSoundScope(true);
        Pick();
        Check(!soundVolume.Enabled, "Selected Sounds, nothing selected: the sliders are off");
        if (shownSounds.FirstOrDefault(r => r.Sound.Stop != null && CanChange(r)) is { } loop) { Pick(loop); Check(soundVolume.Enabled, "a looping sound selected: the sliders are on"); }
        Pick(plain);
        Check(soundVolume.Enabled, "a stock sound selected: the sliders are on");
        soundVolume.TestKey(Keys.Left);
        Check(Math.Abs((SoundEntry(plain.Sound)?.Volume ?? 1) - 0.95f) < 0.001f, "a stock sound takes a volume");
        Check(Cell(plain).StartsWith("This Sound: Volume 95 %"), $"its Settings cell says so ({Cell(plain)})");
        SoundsBackToSet();
        Check(SoundEntry(plain.Sound) == null && Cell(plain) == "", "Back to the Set with no set: nothing changed any more");
        string la = PowerSounds.Leaf(a.Sound.Event), lb = PowerSounds.Leaf(b.Sound.Event);
        await PutSounds(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [la] = tone, [lb] = tone }, "Replace 2 Sounds");
        int steps = audioUndo.Count;
        Pick(a);
        Check(soundVolume.Enabled, "a replaced sound selected: the sliders are on");
        soundVolume.TestKey(Keys.Left);
        Check(Math.Abs((SoundEntry(a.Sound)!.Volume ?? 1) - 0.95f) < 0.001f && audioUndo.Count == steps + 1, $"volume down a step: one Undo step ({audioUndo.Peek().Label})");
        Check(SoundEntry(b.Sound)!.Volume == null, "the other sound keeps its volume");
        Check(soundRows.SelectedRows.Count == 1 && soundRows.SelectedRows[0].Tag == a, "the row stays selected");
        soundFilters[0].TestKey(Keys.Right);
        Check(SoundEntry(a.Sound)!.Reverb > 0.04f, "a filter is set");
        Pick(a, b);
        soundFilters[1].TestKey(Keys.Right);
        Check(SoundEntry(a.Sound)!.Echo > 0.04f && SoundEntry(b.Sound)!.Echo > 0.04f && SoundEntry(b.Sound)!.Reverb == 0 && SoundEntry(a.Sound)!.Reverb > 0.04f, "two selected: only the slider moved changes, on both");
        Pick(a);
        Check(Math.Abs(soundVolume.Value - 0.95f) < 0.001f && soundFilters[0].Value > 0.04f, "selecting it shows its settings");
        AudioUndo(); AudioUndo();
        Check(SoundEntry(a.Sound)!.Reverb == 0 && SoundEntry(a.Sound)!.Volume != null && soundFilters[0].Value == 0, "undo: the settings and sliders go back");
        Check(soundRows.SelectedRows.Count == 1 && soundRows.SelectedRows[0].Tag == a, "the row stays selected through Undo");
        AudioRedo();
        Check(SoundEntry(a.Sound)!.Reverb > 0.04f, "redo");
        OriginalSounds([a]);
        Check(SoundEntry(a.Sound) == null, "back to the original: settings gone");
        AudioUndo();
        Check(SoundEntry(a.Sound)?.Reverb > 0.04f, "undo brings them back");
        var before = soundWavs[la];
        int at = audioUndo.Count;
        await PutSounds(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [la] = tone }, "Replace Sound");
        Check(audioUndo.Count == at + 1 && !ReferenceEquals(before, soundWavs[la]), "replacing a replaced sound is a step");
        AudioUndo();
        Check(ReferenceEquals(before, soundWavs[la]), "undo brings the earlier audio back");
        // the whole set
        SetSoundScope(false);
        Check(soundVolume.Enabled && Math.Abs(soundVolume.Value - 1) < 0.001f, "All Sounds: the sliders are the set's, not the row's");
        soundVolume.Value = 0.5f; soundFilters[5].Value = 0.3f;
        Check(SoundEntry(plain.Sound) == null, "moving them changes nothing until Apply");
        ApplySoundSet();   // (the test's Dialog answers Keep Their Settings)
        Check(SoundSetNow() != null && SoundEntry(plain.Sound) is { InSet: true, Volume: 0.5f, Replaced: false }, "Apply to All Sounds: a stock sound follows the set");
        Check(Cell(plain) == "Whole Set" && Cell(b) == "Whole Set", $"Settings: Whole Set ({Cell(plain)}, {Cell(b)})");
        Check(SoundEntry(a.Sound) is { InSet: false, Reverb: > 0.04f } && Cell(a).StartsWith("This Sound"), $"the row with its own settings kept them ({Cell(a)})");
        Check(draft.PowerSounds.Any(e => shownSounds.Any(r => r.Sound.Stop != null && CanChange(r) && SameEntry(e, r.Sound))) || !shownSounds.Any(r => r.Sound.Stop != null && CanChange(r)), "looping sounds are in it");
        Check(soundSetNote.Text.Contains("All Sounds: Volume 50 %, Robot 30 %") && soundSetNote.Text.Contains("With Own Settings"), $"the note ({soundSetNote.Text})");
        SetSoundScope(true);
        Pick(plain);
        Check(Math.Abs(soundVolume.Value - 0.5f) < 0.001f, "Selected Sounds shows what the row plays with");
        soundFilters[0].TestKey(Keys.Right);
        Check(SoundEntry(plain.Sound) is { InSet: false, Volume: 0.5f, Robot: > 0.29f, Reverb: > 0.04f }, "a row leaves the set from the set's settings");
        Check(Cell(plain).StartsWith("This Sound") && Cell(other) == "Whole Set", "only that row changes");
        SoundsBackToSet();
        Check(SoundEntry(plain.Sound) is { InSet: true, Reverb: 0 } && Cell(plain) == "Whole Set", "Back to the Set");
        Pick(other);
        SetSoundShape(e => e.Volume = null); SetSoundShape(e => e.Robot = 0);
        Check(SoundEntry(other.Sound) is { InSet: false, Shaped: false } && Cell(other) == "This Sound: As It Is", $"a row left out of the set ({Cell(other)})");
        SetSoundScope(false);
        Check(Math.Abs(soundVolume.Value - 0.5f) < 0.001f, "back on All Sounds: the set's sliders");
        soundVolume.Value = 0.6f;
        ApplySoundSet();
        Check(SoundEntry(plain.Sound)?.Volume == 0.6f && SoundEntry(other.Sound)?.Shaped == false && SoundEntry(a.Sound)?.Volume != 0.6f, "a new set: followers change, rows kept");
        AudioUndo();
        Check(SoundEntry(plain.Sound)?.Volume == 0.5f && Math.Abs(soundVolume.Value - 0.5f) < 0.001f, "undo: the set and its sliders go back");
        ToggleSound(plain); Check(SoundEntry(plain.Sound) is { Off: true, InSet: true }, "off keeps it in the set"); ToggleSound(plain);
        Check(SoundEntry(plain.Sound) is { Off: false, InSet: true }, "and on");
        RemoveSoundSet();
        Check(SoundSetNow() == null && SoundEntry(plain.Sound) == null && SoundEntry(other.Sound) == null && SoundEntry(b.Sound) is { Replaced: true, Shaped: false } && SoundEntry(a.Sound) is { Reverb: > 0.04f }, "Remove Set: followers back, your audio and rows kept");
        return fails;
    }

    /// <summary>Before saving: the changed sounds into the packages (PowerSounds.Apply). Returns the work folder.</summary>
    string? ApplyPowerSounds(List<string> log) => game == null ? null : PowerSounds.Apply(draft, editing, lib, game, soundWavs, log);
}
