using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Source's summons panel (Kurt, 2026-10-26): with From the Game → Heroes, the bottom third of the Source column lists the
/// summons and power models of the hero picked above, for that costume: a power that summons per costume (Cable's Telepathic
/// Illusion: his Original costume summons the Original illusion) shows the costume's own, or the power's own summon when the
/// costume has none; costume-agnostic ones (Greymalkin Bombardment) always show. A hero's group row shows them all, each
/// with its costumes. A click picks the summon as the source; a right-click also shows it on the Target side, as the list
/// above does.
/// </summary>
sealed partial class ModelPage
{
    readonly CharacterList sourceSummons = new() { Dock = DockStyle.Fill };
    readonly Label sourceSummonsTitle = new() { AutoSize = true, ForeColor = Ui.Subtle, Font = Ui.Bold(8.5f), Margin = new Padding(2, 6, 0, 4) };
    TableLayoutPanel sourceSplit = null!;
    Control sourceSummonsPanel = null!;
    /// <summary>The hero and costume class (null: the hero's group row) the panel shows.</summary>
    (string Hero, string? Costume)? summonsFor;
    bool fillingSummons;

    /// <summary>The Source column's list: the characters above, the summons panel below (shown for From the Game → Heroes).</summary>
    Control SourceLists()
    {
        sourceSplit = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
        sourceSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 67));
        sourceSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        characters.Margin = new Padding(0);
        sourceSplit.Controls.Add(characters, 0, 0);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(sourceSummonsTitle, 0, 0);
        sourceSummons.Margin = new Padding(0);
        panel.Controls.Add(sourceSummons, 0, 1);
        sourceSummonsPanel = panel;
        sourceSplit.Controls.Add(panel, 0, 1);
        sourceSummons.Thumb = it => it.Header || it.Label ? null : PackageThumb(it.Key[GameSrc.Length..]);
        Ui.Tip(sourceSummons, "The summons and power models of the hero picked above, for that costume (a costume can summon its own version: Cable's Original costume summons his Original illusion). Click one to use it as the source; right-click to show it on the Target side too.");
        sourceSummons.SelectedIndexChanged += (_, _) =>
        {
            if (fillingSummons || sourceSummons.SelectedItem is not CharacterList.Item { Header: false, Label: false } it || it.Key == chosenKey) return;
            characters.ClearSelected();
            GameChosen(it.Key);
        };
        sourceSummons.MouseUp += (_, e) =>
        {
            int i = sourceSummons.IndexFromPoint(e.Location);
            if (e.Button != MouseButtons.Right || i < 0 || i >= sourceSummons.Items.Count || sourceSummons.Items[i] is not CharacterList.Item { Header: false, Label: false } it) return;
            sourceSummons.SelectedIndex = i;
            MirrorTarget(it.Key[GameSrc.Length..]);
        };
        characters.SelectedIndexChanged += (_, _) => SummonsForSelection();
        ShowSummonsPanel();
        return sourceSplit;
    }

    /// <summary>The panel shows only for From the Game → Heroes (the list above takes the whole column otherwise).</summary>
    void ShowSummonsPanel()
    {
        bool on = GameMode && sourceKindTab == 0 && Summons.Count > 0;   // (summons parked: WhatsNew.Summons)
        if (sourceSummonsPanel.Visible == on && sourceSplit.RowStyles[1].Height == (on ? 33 : 0)) return;
        sourceSummonsPanel.Visible = on;
        sourceSplit.RowStyles[0] = new RowStyle(SizeType.Percent, on ? 67 : 100);
        sourceSplit.RowStyles[1] = new RowStyle(SizeType.Percent, on ? 33 : 0);
        if (on) FillSourceSummons();
    }

    /// <summary>The hero (and costume) of the row picked above: a costume package, or a hero's group row.</summary>
    void SummonsForSelection()
    {
        if (!GameMode || characters.SelectedItem is not CharacterList.Item it) return;
        if (it.Header && it.Key.StartsWith("group:game ")) summonsFor = (it.Key["group:game ".Length..], null);
        else if (it.Key.StartsWith(GameSrc) && KindOf(it.Key[GameSrc.Length..]) == 0)
        {
            string file = it.Key[GameSrc.Length..];
            summonsFor = (DescribePackage(file).Title, ConditionSwaps.ClassOf(file));
        }
        else return;
        FillSourceSummons();
    }

    /// <summary>A summon's package shown for this costume: costume-agnostic, the costume's own, or (no own one for that power)
    /// the power's own summon (*).</summary>
    static bool ForCostume(List<Summon> rows, string? costume, string hero)
    {
        if (costume == null) return true;
        foreach (var x in rows)
        {
            var set = x.Costumes;
            if (set.Count == 0 || set.Contains(costume)) return true;
            // the power's own summon, when this costume has no override of its own in that power
            if (set.Contains("*") && !Summons.Values.SelectMany(l => l).Any(y => y.Power == x.Power && y.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase) && y.Costumes.Contains(costume)))
                return true;
        }
        return false;
    }

    void FillSourceSummons()
    {
        fillingSummons = true;
        sourceSummons.BeginUpdate(); sourceSummons.Items.Clear();
        if (summonsFor is not var (hero, costume))
        {
            sourceSummonsTitle.Text = "SUMMONS AND POWER MODELS";
            sourceSummons.Items.Add(new CharacterList.Item("label:pick", "Pick a hero or a costume above", "", Label: true));
        }
        else
        {
            var rows = allPackages.Where(p => IsSummon(p.File) && Summons[Path.GetFileName(p.File)][0].Hero.Equals(hero, StringComparison.OrdinalIgnoreCase)
                && ForCostume(Summons[Path.GetFileName(p.File)], costume, hero)).ToList();
            string costumeName = costume == null ? "" : costume.Split('_').Length > 2 ? " · " + CostumeLabel(costume) : " · Base Package";
            sourceSummonsTitle.Text = $"SUMMONS AND POWER MODELS · {Spaced(hero).ToUpperInvariant()}{costumeName.ToUpperInvariant()}";
            foreach (var p in rows)
            {
                int dot = p.Detail.IndexOf(" · ", StringComparison.Ordinal);
                var costumes = Summons[Path.GetFileName(p.File)].SelectMany(x => x.Costumes).Where(c => c != "*").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string detail = dot > 0 ? p.Detail[(dot + 3)..] : p.File;
                if (costume == null && costumes.Count > 0) detail += " · " + string.Join(", ", costumes.Select(CostumeLabel));
                sourceSummons.Items.Add(new CharacterList.Item(GameSrc + p.File, dot > 0 ? p.Detail[..dot] : p.Detail, detail));
            }
            if (rows.Count == 0) sourceSummons.Items.Add(new CharacterList.Item("label:none", "No summons or power models", "", Label: true));
        }
        sourceSummons.EndUpdate();
        if (chosenKey != null) Reselect(sourceSummons, chosenKey);
        fillingSummons = false;
    }

    /// <summary>"marvelplayer_cable_blackops" → "Blackops" (the costume part of a class name, capitalized).</summary>
    static string CostumeLabel(string cls)
    {
        string c = Spaced(string.Join("_", cls.Split('_').Skip(2)));
        return c.Length == 0 ? "Base" : char.ToUpperInvariant(c[0]) + c[1..];
    }

    /// <summary>A summon picked from elsewhere (a saved source, Edit in Blender's own model): its hero's panel, picked there.</summary>
    void ShowSummonSource(string file)
    {
        if (!IsSummon(file)) return;
        summonsFor = (Summons[Path.GetFileName(file)][0].Hero, null);
        FillSourceSummons();
        fillingSummons = true;
        Reselect(sourceSummons, GameSrc + file);
        fillingSummons = false;
    }
}
