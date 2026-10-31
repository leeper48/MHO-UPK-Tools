using System.Text.RegularExpressions;

namespace MhoExtendedModManager.Gui;

/// <summary>The Editor's Power Sounds tab: a hero's power sound effects (play, turn off, export, replace).</summary>
sealed partial class ModEditorView
{
    sealed record SoundRow(string Power, PowerSound Sound);

    DataGridView soundRows = null!;
    readonly DropDown soundHero = new() { Placeholder = "Hero" };
    readonly TextBox soundFind = new() { Width = 220 };
    readonly Label soundStatus = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(14, 8, 0, 0) };
    readonly Dictionary<string, List<SoundRow>> heroSounds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sounds replaced since the last save: event name → WAV in the game's format.</summary>
    readonly Dictionary<string, byte[]> soundWavs = new(StringComparer.OrdinalIgnoreCase);
    List<SoundRow> shownSounds = [];
    bool soundHeroesFilled;
    int soundRun, soundPlayId;

    Control PowerSoundsPage()
    {
        soundRows = Ui.Grid(S, false, ("Power", 200), ("Part", 130), ("When", 140), ("Note", 200), ("Sound Event", 300), ("Package", 0));
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
                menu.Items.Add(new ToolStripMenuItem("Replace This Sound", null, (_, _) => ReplaceSound(picked[0])) { Enabled = picked[0].Sound.Stop == null, ToolTipText = picked[0].Sound.Stop == null ? "Pick an audio file to play instead of this sound." : "A looping sound can't be replaced yet: nothing would stop it. It can be turned off." });
            menu.Items.Add(new ToolStripMenuItem(picked.Count == 1 ? "Export This Sound" : $"Export {picked.Count} Sounds", null, (_, _) => ExportSounds(picked)) { ToolTipText = "Saves them as .WAV files." });
            if (picked.Any(r => SoundEntry(r.Sound)?.Replaced == true)) menu.Items.Add(new ToolStripMenuItem("Back to the Original Sound", null, (_, _) => OriginalSounds(picked)) { ToolTipText = "Takes your file out: the sound plays as it did before." });
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(soundRows, soundRows.PointToClient(Cursor.Position));
        };
        soundFind.TextChanged += (_, _) => FillSounds();
        MhoPackageModifier.Gui.SearchBox.AddClear(soundFind);
        Ui.Tip(soundFind, "Show only sounds whose power, part, event or package has these words.");
        soundHero.Width = (int)(220 * S);
        soundHero.Format = o => AutoTags.DisplayName((string)o) ?? (string)o;
        soundHero.SelectedIndexChanged += (_, _) => { if (soundHero.SelectedItem is string h) LoadSounds(h); };
        Ui.Tip(soundHero, "The hero whose power sounds are listed. It opens on the mod's hero.");
        var find = new Label { Text = "Find", AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 4, 0) };
        var allOn = Ui.FlatButton("Turn All On", () => SetSoundOn(shownSounds, true), tip: "Turn every sound of this hero back on.");
        var stop = Ui.FlatButton("Stop", () => { VoiceAudio.Stop(); soundStatus.Text = ""; }, tip: "Stop the sound that's playing.");
        var export = Ui.FlatButton("Export Sounds", () => ExportSounds(soundRows.SelectedRows.Count > 1 ? PickedSounds() : shownSounds), "Saves the hero's power sounds as .WAV files named after their sound events, with lines.csv saying which power each belongs to. Selected rows: only those.");
        var replace = Ui.FlatButton("Replace Sounds", ReplaceSounds, $"Pick a folder of {VoiceLines.Formats} files named as Export Sounds names them: those sounds are replaced by your files. You don't need all of them. Any rate, mono or stereo. Looping sounds can't be replaced yet. Right-click a row for one sound.");
        var bar = Toolbar([new Label { Text = "Hero", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 4, 0) }, soundHero, find, soundFind, allOn, stop, export, replace, .. AudioButtons(), soundStatus]);
        var page = Page(soundRows, bar, "The sounds of a hero's powers, and of what those powers set off (projectiles, hits, conditions). Click ▶ to hear one, untick one to turn it off, or replace it with your own. The power's package joins the mod on Save, so the change applies to every costume of that hero. Sounds that many heroes share (knockdowns, slows) aren't listed.");
        page.VisibleChanged += (_, _) => { if (page.Visible) FillSoundHeroes(); };
        return page;
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
        shownSounds = soundHero.SelectedItem is string h ? heroSounds.GetValueOrDefault(h) ?? [] : [];
        string[] words = soundFind.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        soundRows.Rows.Clear();
        soundRows.Columns["Sound Event"]!.Width = shownSounds.Select(r => TextRenderer.MeasureText(r.Sound.Event, soundRows.Font).Width).DefaultIfEmpty((int)(300 * S)).Max() + (int)(24 * S);
        foreach (var r in shownSounds)
        {
            var s = r.Sound;
            string part = Pretty(s.Part), when = s.When.Length > 0 ? Ui.TitleCase(s.When.Replace("power_on_", "").Replace("projectile_on_", "").Replace('_', ' ')) : "";
            var e = SoundEntry(s);
            string note = string.Join(" · ", new[] { e?.Replaced == true ? "Your Own Sound" : s.Stop != null ? "Loops" : "", AlsoIn(r) }.Where(x => x.Length > 0));
            if (!words.All(w => $"{r.Power} {part} {when} {note} {s.Event} {s.Package}".Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            int i = soundRows.Rows.Add(e?.Off != true, "▶", r.Power, part, when, note, s.Event, s.Package);
            soundRows.Rows[i].Tag = r;
            soundRows.Rows[i].Cells["play"].ToolTipText = "Play this sound";
            soundRows.Rows[i].DefaultCellStyle.ForeColor = e?.Off == true ? Ui.Subtle : Ui.Text;
        }
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
            if (on && e != null) { e.Off = false; if (!e.Replaced) draft.PowerSounds.Remove(e); }
            else if (!on)
            {
                if (e == null) draft.PowerSounds.Add(e = new PowerSoundEntry { Package = r.Sound.Package, Component = r.Sound.Component, Index = r.Sound.Index, Event = r.Sound.Event });
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
            byte[] wav;
            if (soundWavs.TryGetValue(leaf, out var mine) && SoundEntry(r.Sound)?.Replaced == true) wav = mine;
            else
            {
                string ev = SoundEntry(r.Sound)?.Replaced == true ? r.Sound.Event + PowerSounds.Suffix : r.Sound.Event;
                var packs = draft.SoundPacks.ToList();
                wav = await Task.Run(() => VoiceAudio.ToWav(VoiceAudio.Wem(ev, packs, cooked).Wem));
            }
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
        var loops = matched.Keys.Where(l => shownSounds.Any(r => r.Sound.Stop != null && PowerSounds.Leaf(r.Sound.Event).Equals(l, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (string l in loops) matched.Remove(l);
        if (matched.Count == 0) { Dialog.Show(this, "No file there is named after a sound of this hero that can be replaced. Export Sounds shows the names; looping sounds can't be replaced yet.", "Nothing to Replace", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var skipped = unknown.Concat(loops.Select(l => l + " (loops)")).ToList();
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
                if (e == null) draft.PowerSounds.Add(e = new PowerSoundEntry { Package = r.Sound.Package, Component = r.Sound.Component, Index = r.Sound.Index, Event = r.Sound.Event });
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
            if (!e.Off) draft.PowerSounds.Remove(e);
        }
        AudioRecord(rows.Count == 1 ? $"Back to the Original: {rows[0].Power}, {Pretty(rows[0].Sound.Part)}" : $"Back to the Original: {rows.Count} Sounds", was);
        FillSounds();
    }

    /// <summary>Before saving: the changed sounds into the packages (PowerSounds.Apply). Returns the work folder.</summary>
    string? ApplyPowerSounds(List<string> log) => game == null ? null : PowerSounds.Apply(draft, editing, lib, game, soundWavs, log);
}
