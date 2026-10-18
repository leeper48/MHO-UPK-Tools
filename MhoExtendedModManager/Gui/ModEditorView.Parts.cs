namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Powers tab's Parts list (a user, 2026-10-17: Invisible Woman's hover when she runs a while; Kurt: make it something a
/// user can do): the selected entry's parts (its packages' effects, sounds, attachments and settings such as Can Transition to
/// Hover), each with an On tick. Switching one off is kept in the draft (PartsOff) and written into the packages on Save
/// (PowerParts.Build); switching it on again puts the game's value back. Sounds play with ▶.
/// </summary>
sealed partial class ModEditorView
{
    DataGridView? partsGrid;
    readonly Label partsNote = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 2, 0, 4), MaximumSize = new Size(560, 0) };
    int partsRequest;
    /// <summary>Each package's switches as the game has them (read once per package).</summary>
    readonly Dictionary<string, List<PowerParts.Switch>> partsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A Powers list entry for a hero's own parts ("char:" and the hero's base package): its parts, and a color for
    /// their effects (the dodge's ghosts, a hover's glow).</summary>
    const string CharacterPrefix = PowerColorBuild.CharacterPrefix;
    static bool IsCharacterEntry(string proto) => proto.StartsWith(CharacterPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>One entry per hero the mod has a package of (costume or base): "&lt;Hero&gt;: Character", its base package's parts
    /// (a costume's are empty copies of the base's).</summary>
    List<Fx.PowerList.Power> CharacterEntries()
    {
        var list = new List<Fx.PowerList.Power>();
        foreach (var f in draft.Packages.Select(x => x.File))
        {
            var m = System.Text.RegularExpressions.Regex.Match(f, @"^UC__MarvelPlayer_([A-Za-z0-9]+)(_.+)?_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || m.Groups[1].Value.StartsWith("Audio", StringComparison.OrdinalIgnoreCase)) continue;
            string baseFile = $"UC__MarvelPlayer_{m.Groups[1].Value}_SF.upk";
            if (list.Any(x => x.Prototype.Equals(CharacterPrefix + baseFile, StringComparison.OrdinalIgnoreCase))) continue;
            string name = System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"(?<=[a-z])(?=[A-Z])", " ");
            list.Add(new Fx.PowerList.Power(CharacterPrefix + baseFile, $"{name}: Character", null, []) { HasName = true });
        }
        return list;
    }

    /// <summary>Each Character entry's own effect parts (dodgevfx, onlevelupeffect …), read in the background once per package;
    /// the power bar fills again when they're in.</summary>
    readonly Dictionary<string, List<PowerParts.Switch>> characterParts = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> characterPartsLoading = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A part tile clicked: the part to play once the Parts list has it.</summary>
    string? pendingPart;

    List<Fx.PowerList.Power> CharacterPartEntries(Fx.PowerList.Power c)
    {
        string file = c.Prototype[CharacterPrefix.Length..];
        if (!characterParts.TryGetValue(file, out var parts))
        {
            if (game != null && characterPartsLoading.Add(file))
            {
                var g = game;
                var drafted = draft.Packages.ToList();
                Task.Run(() =>
                {
                    string? src = new Originals(lib.DataFolder, g).Find(file) ?? (StockFiles.For(g.Cooked, file) is var st && File.Exists(st) ? st : null)
                        ?? drafted.FirstOrDefault(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Source;
                    return src != null && File.Exists(src) ? PowerParts.List(file, src).Where(s => s.Kind == "Effect" && s.Original.Length > 0).ToList() : [];
                }).ContinueWith(t =>
                {
                    if (IsDisposed) return;
                    characterParts[file] = t.Status == TaskStatus.RanToCompletion ? t.Result : [];
                    if (characterParts[file].Count > 0) FillPowers();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return [];
        }
        string hero = c.Name.Replace(": Character", "");
        // the dodge first (Kurt's ask), then the rest by name
        return [.. parts.OrderBy(s => s.PartName.Equals("dodgevfx", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .Select(s => new Fx.PowerList.Power(c.Prototype, $"{hero}: {s.Label}", null, []) { HasName = true, Part = s.PartName })];
    }

    /// <summary>A part tile's words: its name without "On" and "Effect", the first and (when there are more) the last word on
    /// two lines, each at most six letters (Dodge; Level / Up; Death / End: a hero's three death skull effects apart).</summary>
    static string PartWord(string name)
    {
        string label = name.Contains(": ") ? name[(name.IndexOf(": ") + 2)..] : name;
        var w = label.Split(' ').Where(x => x.Length > 0 && x is not ("On" or "Effect" or "Sound" or "Vfx")).Select(x => x.Length > 6 ? x[..6] : x).ToList();
        return w.Count == 0 ? label : w.Count == 1 ? w[0] : w[0] + "\n" + w[^1];
    }

    /// <summary>A Character entry selected: its idle in the preview, the color controls left alone, its parts listed.</summary>
    void CharacterSelected(Fx.PowerList.Power p)
    {
        string baseFile = p.Prototype[CharacterPrefix.Length..];
        powerCaption.Text = (EntryOf(p.Prototype) is { } ce && !ce.Color.IsNone ? "Recolored" : "The Game's Colors") + " · This Character's Own Parts (Dodge, Hover …)";
        powerShared.Text = $"Its parts are in {baseFile}; every costume of the hero uses them. Switching one off puts that package in the mod (the game's copy) on Save.";
        if (addParent != null) addParent.Visible = false;
        LoadPalette(p);
        if (powerPreview != null) powerPreview.OverridesOff = false;
        string show = draft.Packages.Select(x => x.File).FirstOrDefault(f => f.StartsWith(baseFile[..^7], StringComparison.OrdinalIgnoreCase)) ?? baseFile;
        powerPreview?.PlayOwn(show, p.Prototype);
        pendingPart = p.Part;
        if (p.Part != null) powerSideTabs?.Select(1);   // (a part tile: its Parts, where it plays)
        ShowParts(p);
        ShowGhosts(p);
    }

    Control PartsSection()
    {
        var box = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, (int)(6 * S), 0, 0), BackColor = Color.Transparent };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.RowStyles.Add(new RowStyle(SizeType.AutoSize)); box.RowStyles.Add(new RowStyle(SizeType.AutoSize)); box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        box.Controls.Add(partsNote, 0, 1);
        partsNote.MaximumSize = new Size((int)(900 * S), 0);
        var g = Ui.Grid(S, false, ("Kind", 86), ("Part", 170), ("Shows / Plays", 0));
        g.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", Width = (int)(42 * S), SortMode = DataGridViewColumnSortMode.NotSortable });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "play", HeaderText = "", Width = (int)(34 * S), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, ForeColor = Ui.Accent, SelectionForeColor = Ui.Accent } });
        g.Dock = DockStyle.Fill; g.MultiSelect = false;
        g.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || g.Rows[e.RowIndex].Tag is not PowerParts.Switch s) return;
            string col = g.Columns[e.ColumnIndex].Name;
            if (col == "play") { PlayPart(s); return; }
            if (col != "on") return;
            if (s.Original.Length == 0) { partsNote.Text = "That part is off in the game already."; return; }
            bool off = PartOff(s) == null;
            if (off) draft.PartsOff.Add(new PartOffEntry { Package = s.Package, Part = s.Part, Switch = s.Key, Original = s.Original });
            else draft.PartsOff.RemoveAll(x => Same(x, s));
            g.Rows[e.RowIndex].Cells["on"].Value = !off;
            g.Rows[e.RowIndex].DefaultCellStyle.ForeColor = off ? Ui.Subtle : Ui.Text;
            partsNote.Text = PartsSummary();
        };
        Ui.Tip(g, "The parts of the selected power or character in the game's files. Untick a part to switch it off (Save writes it into the mod's packages; tick it again for the game's own). Effect: a particle effect (▶ shows it in the 3D view, such as the dodge's ghosts); Sound: its sound events (▶ plays it); Attachment: a prop the power shows (Invisible Woman's disc); Setting: a switch such as Can Transition to Hover.");
        partsGrid = g;
        box.Controls.Add(g, 0, 2);
        // a Character entry's dodge ghosts under its parts (made from the mod's model; Kurt, 2026-10-17)
        box.RowCount = 4; box.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.Controls.Add(GhostsSection(), 0, 3);
        return box;
    }

    static bool Same(PartOffEntry x, PowerParts.Switch s) =>
        x.Package.Equals(s.Package, StringComparison.OrdinalIgnoreCase) && x.Part.Equals(s.Part, StringComparison.OrdinalIgnoreCase) && x.Switch.Equals(s.Key, StringComparison.OrdinalIgnoreCase);

    PartOffEntry? PartOff(PowerParts.Switch s) => draft.PartsOff.FirstOrDefault(x => Same(x, s));

    string PartsSummary()
    {
        int n = draft.PartsOff.Count;
        return n == 0 ? "Untick a part to switch it off for this mod. Save writes it into the packages; the game's own files stay as they are until Apply Changes."
            : $"{n} part(s) off in this mod. Save writes them into its packages (a package the mod didn't have comes in as the game's copy). Tick a part again for the game's own.";
    }

    /// <summary>The selected entry's packages and their switches (read in the background), into the list.</summary>
    void ShowParts(Fx.PowerList.Power p)
    {
        if (partsGrid == null || game == null) return;
        int req = ++partsRequest;
        partsGrid.Rows.Clear();
        partsNote.Text = "Reading the parts…";
        var g = game;
        string? hero = draft.Packages.Select(x => HeroOf.Package(x.File, g.Cooked)).FirstOrDefault(h => h != null);
        string? owner = agentOwner.GetValueOrDefault(p.Prototype);
        var drafted = draft.Packages.ToList();
        Task.Run(() =>
        {
            List<string> files;
            if (IsCharacterEntry(p.Prototype)) files = [p.Prototype[CharacterPrefix.Length..]];
            else if (PowerColorBuild.IsOwn(p.Prototype))
            {
                string f = p.Prototype[PowerColorBuild.OwnPrefix.Length..];
                files = [f];
                // a costume's parts are empty copies of its hero's (Invisible Woman's hover is in UC__MarvelPlayer_InvisibleWoman_SF):
                // the hero's base package too
                var m = System.Text.RegularExpressions.Regex.Match(f, @"^UC__MarvelPlayer_([A-Za-z0-9]+)_.+_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && $"UC__MarvelPlayer_{m.Groups[1].Value}_SF.upk" is var baseFile && File.Exists(Path.Combine(g.Cooked, baseFile))) files.Add(baseFile);
            }
            else if (PowerColorBuild.IsPackage(p.Prototype)) files = [p.Prototype[PowerColorBuild.PackagePrefix.Length..]];
            else
            {
                powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
                files = owner != null ? PowerRecolor.PackagesOfAgent(powerDb, p.Prototype, PowerColorBuild.ClassOf(owner), g.Cooked)
                    : hero != null ? PowerRecolor.PackagesOf(powerDb, p.Prototype, hero, g.Cooked) : [];
            }
            var all = new List<PowerParts.Switch>();
            foreach (string f in files)
            {
                List<PowerParts.Switch>? sw;
                lock (partsCache) partsCache.TryGetValue(f, out sw);
                if (sw == null)
                {
                    // the game's own values (its clean copy); a package only the mod has: the mod's
                    string? src = new Originals(lib.DataFolder, g).Find(f) ?? (StockFiles.For(g.Cooked, f) is var st && File.Exists(st) ? st : null)
                        ?? drafted.FirstOrDefault(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase)).Source;
                    sw = src != null && File.Exists(src) ? PowerParts.List(f, src) : [];
                    lock (partsCache) partsCache[f] = sw;
                }
                all.AddRange(sw);
            }
            return all;
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != partsRequest || partsGrid == null) return;
            if (t.Status != TaskStatus.RanToCompletion) { partsNote.Text = "The parts couldn't be read: " + t.Exception?.GetBaseException().Message; return; }
            foreach (var s in t.Result.OrderBy(s => KindOrder(s.Kind)).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase))
            {
                bool on = s.Original.Length > 0 && PartOff(s) == null;
                int r = partsGrid.Rows.Add(on, s.Kind, s.Label, s.What, s.Kind is "Sound" or "Effect" && s.Original.Length > 0 ? "▶" : "");
                var row = partsGrid.Rows[r];
                row.Tag = s;
                row.DefaultCellStyle.ForeColor = on ? Ui.Text : Ui.Subtle;
                row.Cells["Part"].ToolTipText = $"{s.PartName} ({s.PartClass}) in {s.Package}";
                row.Cells["Shows / Plays"].ToolTipText = s.Kind == "Setting" ? s.What : s.Original.Replace("|", "\n");
            }
            partsNote.Text = t.Result.Count == 0 ? "No parts that can be switched off were found in this entry's packages." : PartsSummary();
            // a part tile clicked: that part plays now, and its row is selected
            if (pendingPart != null)
            {
                string want = pendingPart; pendingPart = null;
                foreach (DataGridViewRow row in partsGrid.Rows)
                    if (row.Tag is PowerParts.Switch ps && ps.PartName.Equals(want, StringComparison.OrdinalIgnoreCase))
                    {
                        partsGrid.ClearSelection(); row.Selected = true; partsGrid.CurrentCell = row.Cells[1];
                        PlayPart(ps, restoringPose);   // (Save and Keep Editing: on the pose that showed)
                        restoringPose = null;
                        break;
                    }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static int KindOrder(string kind) => kind switch { "Setting" => 0, "Attachment" => 1, "Effect" => 2, "Sound" => 3, _ => 4 };

    /// <summary>▶ on an effect: that one effect in the 3D view, on the character over its idle, again with each loop (the
    /// dodge's ghosts, a hover's glow); the package as this mod has it (a rebuilt dodge ghost shows as built). ▶ on a sound:
    /// its start event, from the mod's sound packs or the game's .pck files (as the Voice tab plays).</summary>
    void PlayPart(PowerParts.Switch s, (string Anim, float Frame)? keep = null)
    {
        if (game == null) return;
        if (s.Kind == "Effect")
        {
            powerPreview?.PlayOwn(s.Package, (powerList.SelectedItem as Fx.PowerList.Power)?.Prototype ?? "", s.PartName, keep);
            partsNote.Text = $"Showing {s.Label} in the 3D view (it plays again with each loop of the idle).";
            return;
        }
        string ev = s.Original.Split('|').FirstOrDefault(x => x.Length > 0) ?? "";
        if (ev.Length == 0) return;
        var packs = draft.SoundPacks.ToList();
        var g = game;
        partsNote.Text = "Playing " + s.What;
        Task.Run(() => VoiceAudio.ToWav(VoiceAudio.Wem(ev, packs, g.Cooked).Wem)).ContinueWith(t =>
        {
            if (IsDisposed) return;
            if (t.Status == TaskStatus.RanToCompletion) VoiceAudio.Play(t.Result);
            else partsNote.Text = "That sound wasn't found in the game's sound files: " + t.Exception?.GetBaseException().Message;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Before saving: the parts switched off (or on again) into the draft's packages (PowerParts.Build). Returns the work folder.</summary>
    string? ApplyParts(List<string> log)
    {
        if (game == null) { if (draft.PartsOff.Count > 0) throw new InvalidDataException("the game folder isn't set"); return null; }
        return PowerParts.Build(draft, editing, lib, game, log);
    }
}
