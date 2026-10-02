using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Change Animation (the Animations tab, Kurt 2026-10-02): which character's animation a costume plays for one of its
/// animations. Pinned first: the Source (the character a moved costume came from) and the Target (this costume as the game
/// has it: the original), each pointing at the animation of the same name; below them every hero, team-up and library mod.
/// A character's animations list the same name first, then all the others. Picking one plays it on the costume's model in
/// the tab's 3D view (<see cref="Try"/>); Use This Animation returns it.
/// </summary>
sealed class AnimPickerForm : Form
{
    /// <summary>A character to take animations from: a package (game or mod) read with CostumeAnims.</summary>
    public sealed record Donor(string Title, string Kind, string Path, string File, Mod? Mod, string Key)
    {
        public bool Pinned => Kind is "Source" or "Target";
    }

    public Donor? ChosenDonor { get; private set; }
    public CostumeAnims.Anim? Chosen { get; private set; }
    /// <summary>Copy From a Character (Kurt, 2026-10-02: many at once): the ticked animations, each for the costume's
    /// animation of the same name.</summary>
    public List<CostumeAnims.Anim> ChosenMany { get; private set; } = [];
    /// <summary>Called with the picked animation (play it on the costume's model).</summary>
    public Action<AnimRef>? Try { get; init; }

    readonly string slot;
    readonly string? cooked;
    readonly List<Donor> donors;
    readonly DataGridView who, what;
    readonly TextBox whoFind = new() { Width = 200 }, whatFind = new() { Width = 200 };
    readonly Label status = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 8, 12, 0) };
    readonly Button use;
    readonly Dictionary<string, Task<CostumeAnims?>> read = new(StringComparer.OrdinalIgnoreCase);
    CostumeAnims? shown;
    Donor? shownDonor;
    // Many at once: the costume's animation names (only those are offered), what each is (for Find), and the ones untouched.
    readonly HashSet<string>? match;
    readonly Func<string, string>? kindOf;
    readonly HashSet<string> unticked = new(StringComparer.OrdinalIgnoreCase);
    bool Multi => match != null;

    /// <param name="matchSlots">Copy From a Character: the costume's animation names; the window then offers the chosen
    /// character's animations of those names, each with a tick box (all ticked), and returns the ticked ones.</param>
    public AnimPickerForm(string slot, List<Donor> donors, string? cooked, IEnumerable<string>? matchSlots = null, Func<string, string>? kindOf = null)
    {
        this.slot = slot; this.donors = donors; this.cooked = cooked; this.kindOf = kindOf;
        match = matchSlots?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Text = Multi ? "Copy From a Character" : "Change Animation";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(12);
        float s = DeviceDpi / 96f;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var head = new Label { Text = Multi ? "Use a Character's Animations of the Same Names:" : $"Change \"{slot}\" To:", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 2, 0, 8) };
        root.Controls.Add(head, 0, 0); root.SetColumnSpan(head, 2);
        root.Controls.Add(Bar(new Label { Text = "CHARACTER", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 10, 0) }, whoFind), 0, 1);
        root.Controls.Add(Bar(new Label { Text = "ANIMATION", AutoSize = true, Tag = "subtle", Margin = new Padding(10, 6, 10, 0) }, whatFind), 1, 1);
        who = Ui.Grid(s, false, ("Character", 0), ("From", 110));
        who.Columns[0].Name = "who"; who.Columns[1].Name = "kind";
        who.MultiSelect = false;
        what = Ui.Grid(s, false, ("Animation", 0), ("Kind", 110), ("Frames", 70), ("Seconds", 70));
        what.Columns[0].Name = "anim"; what.Columns[1].Name = "kind"; what.Columns[2].Name = "frames"; what.Columns[3].Name = "secs";
        if (!Multi) what.Columns["kind"]!.Visible = false;
        else
        {
            what.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "Use", Width = (int)(46 * s), SortMode = DataGridViewColumnSortMode.NotSortable });
            what.CellClick += (_, e) =>
            {
                if (e.RowIndex < 0 || what.Columns[e.ColumnIndex].Name != "use" || what.Rows[e.RowIndex].Tag is not CostumeAnims.Anim a) return;
                if (!unticked.Remove(a.Name)) unticked.Add(a.Name);
                what.Rows[e.RowIndex].Cells["use"].Value = !unticked.Contains(a.Name);
                UpdateUse();
            };
        }
        what.MultiSelect = false;
        what.Margin = new Padding(10, 0, 0, 0);
        root.Controls.Add(who, 0, 2);
        root.Controls.Add(what, 1, 2);
        var bar = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        var cancel = Ui.FlatButton("Cancel", () => DialogResult = DialogResult.Cancel, "Change nothing (Esc).");
        use = Multi
            ? Ui.AccentButton("Use Ticked", Use, "The costume plays each ticked animation in place of its own of the same name (Enter). Saved with the mod.")
            : Ui.AccentButton("Use This Animation", Use, "The costume plays the selected animation in place of its own (Enter). Saved with the mod.");
        use.Enabled = false;
        bar.Controls.AddRange([cancel, use]);
        if (Multi)
        {
            bar.Controls.Add(Ui.FlatButton("Tick None", () => Tick(false), "Untick every animation the list shows (Find narrows it, for example to Emote or Movement)."));
            bar.Controls.Add(Ui.FlatButton("Tick All", () => Tick(true), "Tick every animation the list shows."));
        }
        bar.Controls.Add(status);
        root.Controls.Add(bar, 0, 3); root.SetColumnSpan(bar, 2);
        Controls.Add(root);
        CancelButton = cancel; AcceptButton = use;

        SearchBox.AddClear(whoFind); SearchBox.AddClear(whatFind);
        Ui.Tip(whoFind, "Show only characters whose name has these letters.");
        Ui.Tip(whatFind, Multi ? "Show only animations whose name or kind (Idle, Emote, Movement, Attack …) has these letters." : "Show only animations whose name has these letters.");
        whoFind.TextChanged += (_, _) => FillWho();
        whatFind.TextChanged += (_, _) => FillWhat();
        who.SelectionChanged += (_, _) => { if (who.SelectedRows.Count > 0 && who.SelectedRows[0].Tag is Donor d) ShowDonor(d); };
        what.SelectionChanged += (_, _) =>
        {
            if (what.SelectedRows.Count == 0 || what.SelectedRows[0].Tag is not CostumeAnims.Anim a) { if (!Multi) use.Enabled = false; return; }
            if (!Multi) use.Enabled = true;
            Try?.Invoke(a.Ref);
        };
        what.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && !Multi) Use(); };

        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.RestyleButtons(this);
        Ui.FitToScreen(this, 1000, 680);
        FillWho();
        Shown += (_, _) => { if (who.Rows.Count > 0) { who.ClearSelection(); who.Rows[0].Selected = true; } };
    }

    static FlowLayoutPanel Bar(params Control[] c) { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) }; f.Controls.AddRange(c); return f; }

    void FillWho()
    {
        string f = whoFind.Text.Trim();
        who.Rows.Clear();
        foreach (var d in donors.Where(d => d.Pinned || f.Length == 0 || d.Title.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            int i = who.Rows.Add(d.Title, d.Kind);
            who.Rows[i].Tag = d;
            if (d.Pinned) { who.Rows[i].DefaultCellStyle.ForeColor = Ui.TagCharacter; who.Rows[i].DefaultCellStyle.SelectionForeColor = Ui.TagCharacter; }
            who.Rows[i].Cells["who"].ToolTipText = d.Kind switch
            {
                "Source" => "The character this costume's model came from: its animation of the same name.",
                "Target" => "This costume as the game has it: its own animation (choosing it undoes a change).",
                _ => $"{d.File}",
            };
        }
    }

    void ShowDonor(Donor d)
    {
        shownDonor = d; shown = null;
        unticked.Clear();
        what.Rows.Clear();
        status.Text = "Reading…";
        if (!read.TryGetValue(d.Key, out var t))
            read[d.Key] = t = Task.Run(() => { try { return CostumeAnims.Read(d.Path, d.File, CostumeAnims.FilesFor(d.Mod, cooked)); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return null; } });
        t.ContinueWith(r =>
        {
            if (IsDisposed || shownDonor != d) return;
            shown = r.Result;
            status.Text = shown == null ? "No Animations Found for This Character" : "";
            FillWhat();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The same name first (gold), then every other animation.</summary>
    void FillWhat()
    {
        what.Rows.Clear();
        if (shown == null) return;
        string f = whatFind.Text.Trim();
        var same = Multi ? [] : shown.Anims.Where(a => a.Name.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList();
        var rest = Multi
            ? shown.Anims.Where(a => match!.Contains(a.Name) && (f.Length == 0 || a.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || (kindOf?.Invoke(a.Name) ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)))
            : shown.Anims.Where(a => !same.Contains(a) && (f.Length == 0 || a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        foreach (var a in same.Concat(rest))
        {
            int i = what.Rows.Add();
            var row = what.Rows[i];
            row.Cells["anim"].Value = a.Name;
            row.Cells["kind"].Value = kindOf?.Invoke(a.Name) ?? "";
            if (Multi) row.Cells["use"].Value = !unticked.Contains(a.Name);
            row.Tag = a;
            if (same.Contains(a))
            {
                row.Cells["anim"].Value = a.Name + "  (Same Name)";
                row.DefaultCellStyle.ForeColor = Ui.TagCharacter; row.DefaultCellStyle.SelectionForeColor = Ui.TagCharacter;
            }
            row.Cells["anim"].ToolTipText = $"{a.From.PackageName}.upk · {a.From.Path}";
        }
        if (what.Rows.Count > 0) { if (!Multi) { what.ClearSelection(); what.Rows[0].Selected = true; } }
        else status.Text = Multi ? (f.Length > 0 ? $"No \"{f}\" Animations of the Same Names" : "No Animations of the Same Names") : $"No \"{f}\" Animations";
        UpdateUse();
        // Length per row in the background (a decode each).
        var rows = what.Rows.Cast<DataGridViewRow>().Select(r => (r, (CostumeAnims.Anim)r.Tag!)).ToList();
        var forShown = shown;
        Task.Run(() => rows.Select(x => (x.r, Span: ModAnimations.Load(x.Item2.Ref) is { } b ? MeshAnimator.Span(b) : ((float, float)?)null)).ToList()).ContinueWith(t =>
        {
            if (IsDisposed || shown != forShown) return;
            foreach (var (r, span) in t.Result)
                if (span is { } sp && r.DataGridView != null) { r.Cells["frames"].Value = $"{sp.Item1:0}"; r.Cells["secs"].Value = $"{sp.Item2:0.00}"; }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Test hook: Find set to <paramref name="find"/>, Tick None, then Tick All (the shown ones).</summary>
    public void TickForTest(string find) { whatFind.Text = find; Tick(false); }

    /// <summary>Test hook: selects the first character whose title has <paramref name="title"/> (null: the first row).</summary>
    public void PickForTest(string? title)
    {
        foreach (DataGridViewRow r in who.Rows)
            if (r.Tag is Donor d && (title == null || d.Title.Contains(title, StringComparison.OrdinalIgnoreCase))) { who.ClearSelection(); r.Selected = true; return; }
    }

    /// <summary>Every animation of the same name not unticked (also those Find hides).</summary>
    List<CostumeAnims.Anim> Ticked() => shown == null || match == null ? [] : [.. shown.Anims.Where(a => match.Contains(a.Name) && !unticked.Contains(a.Name))];

    void UpdateUse()
    {
        if (!Multi) return;
        int n = Ticked().Count;
        use.Text = $"Use Ticked ({n})";
        use.Enabled = n > 0;
    }

    void Tick(bool on)
    {
        foreach (DataGridViewRow r in what.Rows)
            if (r.Tag is CostumeAnims.Anim a) { if (on) unticked.Remove(a.Name); else unticked.Add(a.Name); r.Cells["use"].Value = on; }
        UpdateUse();
    }

    void Use()
    {
        if (Multi)
        {
            if (shownDonor == null || Ticked() is not { Count: > 0 } t) return;
            ChosenMany = t; ChosenDonor = shownDonor;
            DialogResult = DialogResult.OK;
            return;
        }
        if (what.SelectedRows.Count == 0 || what.SelectedRows[0].Tag is not CostumeAnims.Anim a || shownDonor == null) return;
        Chosen = a; ChosenDonor = shownDonor;
        DialogResult = DialogResult.OK;
    }

    /// <summary>
    /// The characters to offer for a costume package of <paramref name="mod"/>: its Source (the manifest's MovedFrom, or the
    /// note a move wrote) and Target (the package as the game has it), then every hero (base package), team-up, and the
    /// other mods' character packages.
    /// </summary>
    public static List<Donor> Donors(Mod mod, string packageFile, List<Costume>? costumes, ModLibrary lib, string cooked)
    {
        var list = new List<Donor>();
        string? moved = mod.Manifest.MovedFrom;
        if (moved == null && System.Text.RegularExpressions.Regex.Match(mod.Manifest.Notes ?? "", @"Moved from .*?\(([^()→]+?) → [^()]+\)") is { Success: true } m) moved = m.Groups[1].Value.Trim();
        string Name(Costume c) => c.IsTeamUp ? $"{c.Title} (Team-Up)" : $"{AutoTags.DisplayName(c.Class.Split('_')[1]) ?? c.Class.Split('_')[1]} {c.Title}";
        if (moved != null && costumes?.FirstOrDefault(c => c.Short.Replace(".prototype", "").Equals(moved, StringComparison.OrdinalIgnoreCase)) is { } src)
        {
            string p = StockFiles.For(cooked, src.Package);
            if (File.Exists(p)) list.Add(new Donor(Name(src), "Source", p, src.Package, null, "source|" + src.Package));
        }
        string tp = StockFiles.For(cooked, packageFile);
        if (File.Exists(tp))
        {
            string title = costumes?.FirstOrDefault(c => c.Package.Equals(packageFile, StringComparison.OrdinalIgnoreCase)) is { } tc ? Name(tc) : System.IO.Path.GetFileNameWithoutExtension(packageFile);
            list.Add(new Donor(title + " (the Original)", "Target", tp, packageFile, null, "target|" + packageFile));
        }
        // Heroes: each one's base package (its default animations); team-ups.
        if (costumes != null)
        {
            foreach (var h in costumes.Where(c => !c.IsTeamUp).Select(c => c.Class.Split('_')).Where(p => p.Length >= 2).Select(p => p[1]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string file = $"UC__MarvelPlayer_{h}_SF.upk", p = StockFiles.For(cooked, file);
                if (File.Exists(p)) list.Add(new Donor(AutoTags.DisplayName(h) ?? h, "Hero", p, file, null, "hero|" + file));
            }
            foreach (var t in costumes.Where(c => c.IsTeamUp).GroupBy(c => c.Package, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                string p = StockFiles.For(cooked, t.Package);
                if (File.Exists(p)) list.Add(new Donor($"{t.Title} (Team-Up)", "Team-Up", p, t.Package, null, "teamup|" + t.Package));
            }
        }
        foreach (var om in lib.Mods.Where(x => x.FolderName != mod.FolderName))
            foreach (string f in om.Manifest.UpkReplacements.Where(f => f.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || f.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase)))
            {
                string p = System.IO.Path.Combine(om.Folder, f);
                if (File.Exists(p)) list.Add(new Donor(om.Manifest.UpkReplacements.Count(x => x.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) > 1 ? $"{om.Name} · {f}" : om.Name, "Mod", p, f, om, "mod|" + om.FolderName + "|" + f));
            }
        return [.. list.Where(d => d.Pinned), .. list.Where(d => !d.Pinned).OrderBy(d => d.Kind == "Mod").ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase)];
    }
}
