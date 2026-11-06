using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Move to Another Costume (Kurt, 2026-09-29): the plan for moving a single-costume mod onto another costume of the same
/// hero, in the app's dialog style: the two costumes' store images, what moves, what's left out, the mods already on the
/// target, and one choice (turn the new mod on and the original off). Create Mod (Enter) or Cancel (Esc). The work itself
/// (CostumeMove.CreateMod) runs in MainForm after the window closes.
/// </summary>
sealed class MoveCostumeForm : Form
{
    readonly CheckBox swap;
    public bool SwapOn => swap.Checked;

    // The Name row (Kurt, 2026-11-06): the name the target shows in game, editable, with who else shows that string.
    readonly CostumeMove.NameInfo? naming;
    readonly CheckBox? setName;
    readonly TextBox? nameBox;
    /// <summary>For CreateMod: null = as planned (no Name row), "" = leave the game's name, else that name.</summary>
    public string? NameChoice => naming == null || setName == null || nameBox == null ? null
        : setName.Checked && nameBox.Text.Trim().Length > 0 && (naming.Planned || nameBox.Text.Trim() != naming.Current) ? nameBox.Text.Trim() : "";

    readonly bool cross;

    /// <param name="crossHero">A move to another hero (CrossMove): the model goes into the target costume's own package; sounds stay.</param>
    /// <param name="naming">The target's name (CostumeMove.Naming): adds the Name row.</param>
    public MoveCostumeForm(Mod mod, CostumeMove.Plan plan, StockCatalog? catalog, IReadOnlyList<Mod> onTarget, bool crossHero = false, CostumeMove.NameInfo? naming = null)
    {
        cross = crossHero;
        this.naming = naming;
        Label? warnNote = null;
        Text = "Move to Another Costume";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        for (int i = 0; i < 6; i++) t.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, 100));
        string targetName = crossHero ? $"{HeroOf(plan.Target)} {plan.Target.Title}" : plan.Target.Title;
        t.Controls.Add(new Label { Text = Ui.TitleCase($"Move to {targetName}"), AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 8) }, 0, 0);

        // The two costumes' store images (the game's own), source → target.
        var pics = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 8) };
        // The source shows the mod's own store image when it has one (that's what moves), else the game's.
        var modStore = plan.Icons.FirstOrDefault(i => i.Kind == "Store Image");
        pics.Controls.Add(Card(plan.Source, catalog, s, modStore == null ? null : Ui.DdsThumb(Path.Combine(mod.Folder, modStore.Dds), 420), mod.Name));
        pics.Controls.Add(new Label { Text = "→", AutoSize = true, Font = Ui.Bold(20f), Anchor = AnchorStyles.None, Margin = new Padding((int)(10 * s), 0, (int)(10 * s), 0) });
        pics.Controls.Add(crossHero ? Card(plan.Target, catalog, s, null, null, $"{HeroOf(plan.Target)} {plan.Target.Title}") : Card(plan.Target, catalog, s));
        t.Controls.Add(pics, 0, 1);

        // (with a Name row, the name isn't listed as moving text: the row says what it becomes)
        var shown = naming == null ? plan : plan with { Strings = [.. plan.Strings.Where(x => x.To != plan.Target.DisplayName)] };
        var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = false, Text = crossHero ? CrossSummary(mod, shown, onTarget) : Summary(mod, shown, onTarget) };
        t.Controls.Add(box, 0, 2);

        if (naming != null)
        {
            string what = plan.Target.IsTeamUp ? "team-up" : "costume";
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 8, 0, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            setName = new CheckBox { Text = "Name in Game", AutoSize = true, Checked = naming.Planned, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 8, 0) };
            Ui.Tip(setName, $"Give the {what} this name in game. Unticked, it keeps the game's name (\"{CostumeMove.Untagged(naming.Current)}\").");
            nameBox = new TextBox { Text = naming.Suggested, Dock = DockStyle.Fill, Enabled = setName.Checked };
            setName.CheckedChanged += (_, _) => { nameBox.Enabled = setName.Checked; if (setName.Checked) nameBox.Focus(); };
            var note = new Label { AutoSize = true, MaximumSize = new Size((int)(430 * s), 0), Margin = new Padding(0, 4, 0, 0) };
            if (naming.AlsoUsedBy.Count == 0) { note.Text = $"Only this {what} shows this name (the game's: \"{CostumeMove.Untagged(naming.Current)}\")."; note.Tag = "subtle"; }
            else
            {
                note.Text = $"The game also shows this name for {string.Join(", ", naming.AlsoUsedBy.Take(4))}{(naming.AlsoUsedBy.Count > 4 ? $" and {naming.AlsoUsedBy.Count - 4} more" : "")}: a new name changes theirs too.";
                warnNote = note;
            }
            row.Controls.Add(setName, 0, 0); row.Controls.Add(nameBox, 1, 0);
            row.Controls.Add(note, 1, 1);
            t.Controls.Add(row, 0, 3);
        }

        swap = new CheckBox { Text = $"Turn the new mod on and \"{mod.Name}\" off", AutoSize = true, Checked = true, Margin = new Padding(0, 8, 0, 0) };
        Ui.Tip(swap, "The costume then shows on its new slot after Apply Changes. Leave it unticked to decide later.");
        t.Controls.Add(swap, 0, 4);

        var bar = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
        var cancel = Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; }, "Change nothing (Esc).");
        var create = Ui.AccentButton("Create Mod", () => { DialogResult = DialogResult.OK; }, "Add the moved costume as a new mod at the top of the list; the original isn't changed (Enter).");
        create.Enabled = crossHero || (plan.Problems.Count == 0 && plan.Packages.Count > 0);
        bar.Controls.Add(cancel); bar.Controls.Add(create);
        t.Controls.Add(bar, 0, 5);
        Controls.Add(t);
        AcceptButton = create.Enabled ? create : cancel;
        CancelButton = cancel;
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.RestyleButtons(this);
        if (warnNote != null) warnNote.ForeColor = Ui.OverrideAmber;   // (after the theme, which recolors labels)
        Ui.FitToScreen(this, 620, 600);
        Shown += (_, _) => { box.SelectionLength = 0; ActiveControl = create.Enabled ? create : cancel; };
    }

    /// <summary>A costume's store image (the game's) with its name under it.</summary>
    static Control Card(Costume c, StockCatalog? catalog, float s, Image? own = null, string? caption = null, string? title = null)
    {
        var p = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Anchor = AnchorStyles.None };
        var pic = new PictureBox { Size = new Size((int)(120 * s), (int)(168 * s)), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0), Tag = "card" };
        if (own != null) pic.Image = own;
        else if (Image(c, catalog) is Bitmap b) pic.Image = b;
        p.Controls.Add(pic, 0, 0);
        p.Controls.Add(new Label { Text = caption == null ? title ?? c.Title : caption + Environment.NewLine + $"({c.Title})", AutoSize = true, Anchor = AnchorStyles.None, TextAlign = ContentAlignment.TopCenter, MaximumSize = new Size((int)(220 * s), 0), Margin = new Padding(0, 4, 0, 0) }, 0, 1);
        return p;
    }

    /// <summary>The game's store image of a costume (for the menu and this window), or null.</summary>
    public static Bitmap? Image(Costume c, StockCatalog? catalog)
    {
        if (catalog == null || Costume.IconTexture(c.Store) is not { } st) return null;
        try
        {
            lock (Ui.StockLock)
                return catalog.Preview(st.Package, st.Texture) is { } pv ? MhoPackageModifier.TextureDecode.ToBitmap(pv.Bgra, pv.W, pv.H) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return null; }
    }

    /// <summary>"Storm" from Entity/Characters/Avatars/Shipping/Storm.prototype.</summary>
    static string HeroOf(Costume c)
    {
        if (c.IsTeamUp) return "Team-Up";
        string id = Path.GetFileNameWithoutExtension((c.Hero ?? c.Short).Replace('\\', '/').Split('/')[^1]);
        return AutoTags.DisplayName(id) ?? id;
    }

    static string CrossSummary(Mod mod, CostumeMove.Plan plan, IReadOnlyList<Mod> onTarget)
    {
        var sb = new System.Text.StringBuilder();
        void Line(string x = "") => sb.Append(x).Append("\r\n");
        string hero = HeroOf(plan.Target), from = HeroOf(plan.Source);
        string who = plan.Target.IsTeamUp ? plan.Target.Title : hero;   // (whose animations, powers, skeleton: "Old Man Thor's", not "Team-Up's")
        Line("What moves:");
        Line($"  • the model, with its materials and textures, into {plan.Target.Package}");
        if (plan.Icons.Count > 0) Line("  • " + string.Join(", ", plan.Icons.Select(i => i.Kind.ToLowerInvariant())) + $", under {hero} {plan.Target.Title}'s names");
        foreach (var st in plan.Strings) Line($"  • the costume text \"{st.Text}\" ({st.Language}), as {hero} {plan.Target.Title}'s");
        Line(mod.Manifest.AudioPacks.Count > 0
            ? $"  • the voice: the mod's own voice lines (its sound pack comes along)"
            : $"  • the voice: {from}'s lines, played by {who}'s powers");
        Line();
        Line($"What stays {who}'s:");
        Line($"  • the animations and powers. Parts of the model {who}'s skeleton doesn't move (hair, a cape) stay still.");
        Line();
        var others = onTarget.Where(m => m != mod).ToList();
        if (others.Count > 0)
        {
            Line($"Other mods for {hero} {plan.Target.Title} (the one higher in the list wins):");
            foreach (var o in others) Line($"  • \"{o.Name}\"" + (o.Enabled ? " (on)" : ""));
            Line();
        }
        Line($"The new mod \"{CrossMove.NewName(mod, plan.Target)}\" goes at the top of the list. \"{mod.Name}\" isn't changed.");
        return sb.ToString();
    }

    static string Summary(Mod mod, CostumeMove.Plan plan, IReadOnlyList<Mod> onTarget)
    {
        var sb = new System.Text.StringBuilder();
        void Line(string x = "") => sb.Append(x).Append("\r\n");
        if (plan.Problems.Count > 0)
        {
            Line("Can't move this costume:");
            foreach (var p in plan.Problems) Line("  • " + p);
            Line();
        }
        Line("What moves:");
        foreach (var pk in plan.Packages)
        {
            var parts = new List<string>();
            if (pk.Meshes > 0) parts.Add($"{pk.Meshes} model(s)");
            if (pk.Animations > 0) parts.Add($"{pk.Animations} animation(s)");
            if (pk.Weapon) parts.Add("its weapon / prop");
            Line($"  • the package, as {pk.TargetFile}" + (parts.Count > 0 ? $": {string.Join(", ", parts)}" : ""));
        }
        if (plan.Icons.Count > 0) Line("  • " + string.Join(", ", plan.Icons.Select(i => i.Kind.ToLowerInvariant())) + $", under {plan.Target.Title}'s names");
        foreach (var sp in plan.SoundPacks) Line($"  • the sound pack {sp}");
        foreach (var st in plan.Strings) Line($"  • the costume text \"{st.Text}\" ({st.Language}), as {plan.Target.Title}'s");
        if (plan.Left.Count > 0)
        {
            Line();
            Line("Left out (they stay in the original mod):");
            foreach (var l in plan.Left) Line("  • " + l);
        }
        Line();
        var others = onTarget.Where(m => m != mod).ToList();
        if (others.Count > 0)
        {
            Line($"Other mods for {plan.Target.Title} (the one higher in the list wins):");
            foreach (var o in others) Line($"  • \"{o.Name}\"" + (o.Enabled ? " (on)" : ""));
            Line();
        }
        Line($"The new mod \"{mod.Name} (on {plan.Target.Title})\" goes at the top of the list. \"{mod.Name}\" isn't changed.");
        return sb.ToString();
    }
}
