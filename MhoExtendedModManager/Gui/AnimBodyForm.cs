using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Bone Lengths and Shape Keys (Kurt, 2026-11-17: Carnage's Vicious Lunge stretched Medusa's fingers, and his attacks drive
/// shape keys on his arms): for one animation, the bones it moves (their animated positions; ticked = Keep Bone Length, the
/// model's own length, only turning) and the shape-key curves it plays (ticked = plays). OK gives the two lists.
/// </summary>
sealed class AnimBodyForm : Form
{
    /// <summary>A bone the animation moves: how far its keys reach, whether it's kept now, whether the game takes its positions.</summary>
    public sealed record Bone(string Name, float Reach, bool Kept, bool Used);

    readonly DataGridView bones, curves, turns;
    readonly LightSlider stillSlider;
    public List<string> KeepLengths { get; private set; } = [];
    public List<string> CurvesOff { get; private set; } = [];
    /// <summary>Keep Bones from Turning (Kurt, 2026-11-20: a car on Ghost Rider's bike banked in turns): the bones whose turns the
    /// copy takes only <see cref="StillStrength"/> of.</summary>
    public List<string> StillBones { get; private set; } = [];
    public float StillStrength { get; private set; }

    /// <param name="turnRows">The bones the animation turns: how far (degrees, the most of any key), whether held now.</param>
    public AnimBodyForm(string anim, IReadOnlyList<Bone> boneRows, IReadOnlyList<(string Name, bool Off)> curveRows,
        IReadOnlyList<(string Name, float Degrees, bool Still)>? turnRows = null, float stillStrength = 0)
    {
        turnRows ??= [];
        Text = $"Bone Lengths and Shape Keys: {anim}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = new Label
        {
            AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(760 * s), 0), Margin = new Padding(0, 0, 0, 10),
            Text = "Only this costume's copy of the animation changes (the character it comes from keeps its own). An animation that wasn't changed yet becomes a copy of itself, listed as changed (Back to the Original takes it all off).",
        };
        t.Controls.Add(hint, 0, 0); t.SetColumnSpan(hint, 2);
        Label Head(string text) => new() { Text = text, AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(2, 0, 0, 4) };
        t.Controls.Add(Head("KEEP BONE LENGTHS"), 0, 1);
        t.Controls.Add(Head("SHAPE KEYS"), 1, 1);

        bones = Ui.Grid(s, false, ("Bone", 170), ("Moves Up To", 100), ("", 0));
        bones.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "keep", HeaderText = "Keep", Width = (int)(50 * s), SortMode = DataGridViewColumnSortMode.NotSortable });
        bones.Columns[3].Name = "note";
        foreach (var b in boneRows.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            int i = bones.Rows.Add(b.Kept, b.Name, $"{b.Reach:0.#} units", b.Used || b.Kept ? "" : "The game keeps its length already");
            bones.Rows[i].Tag = b.Name;
            if (!b.Used && !b.Kept) { locked.Add(bones.Rows[i]); bones.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle; }
        }
        Toggle(bones, "keep");
        Ui.Tip(bones, "The bones this animation slides (not only turns): ticked keeps the model's own bone length, so it only turns (Carnage's Vicious Lunge stretches his fingers into claws: ticking his finger bones keeps a new model's hand its own size). Grayed: the game already keeps that bone's length for this animation.");
        t.Controls.Add(bones, 0, 2);
        curves = Ui.Grid(s, false, ("Shape Key Curve", 0));
        curves.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "Plays", Width = (int)(52 * s), SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var (name, off) in curveRows) { int i = curves.Rows.Add(!off, name); curves.Rows[i].Tag = name; }
        Toggle(curves, "on");
        Ui.Tip(curves, "The shape keys (morph targets) this animation drives, by curve name (Carnage's blades, mace and hand scale): untick one so this costume's copy doesn't play it (a new model doesn't have the original's shape keys). The 3D preview doesn't show shape keys; check them in game.");
        curves.Margin = new Padding(10, 0, 0, 0);
        t.Controls.Add(curves, 1, 2);
        if (curveRows.Count == 0) { curves.Visible = false; var none = new Label { Text = "This animation drives no shape keys.", AutoSize = true, Tag = "subtle", Margin = new Padding(10, 4, 0, 0) }; t.Controls.Add(none, 1, 2); }
        if (boneRows.Count == 0) { bones.Visible = false; var none = new Label { Text = "This animation slides no bones (it only turns them).", AutoSize = true, Tag = "subtle", Margin = new Padding(2, 4, 0, 0) }; t.Controls.Add(none, 0, 2); }

        var quick = Ui.FlatButton("Fingers", () => TickWhere(n => IsFinger(n)), "Tick every finger and thumb bone (both hands).");
        var allMoved = Ui.FlatButton("All", () => TickWhere(_ => true), "Tick every bone the animation slides.");
        var noneB = Ui.FlatButton("None", () => { foreach (DataGridViewRow r in bones.Rows) if (!locked.Contains(r)) r.Cells["keep"].Value = false; }, "Untick every bone: the animation's own lengths.");
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([quick, allMoved, noneB]);
        var ok = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        ok.Controls.AddRange([
            Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, "Close without changing the animation (Esc)."),
            Ui.AccentButton("OK", Accept, "Make this costume's copy of the animation with these choices (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 3);

        // Keep Bones from Turning: the bones the animation turns, ticked = their turn kept at the slider's strength
        var turnHead = Head("KEEP BONES FROM TURNING");
        turnHead.Margin = new Padding(2, 14, 0, 4);
        t.Controls.Add(turnHead, 0, 4); t.SetColumnSpan(turnHead, 2);
        turns = Ui.Grid(s, false, ("Bone", 220), ("Turns Up To", 110), ("", 0));
        turns.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "still", HeaderText = "Keep", Width = (int)(50 * s), SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var r in turnRows.OrderByDescending(r => r.Still).ThenByDescending(r => r.Degrees))
        {
            int i = turns.Rows.Add(r.Still, r.Name, $"{r.Degrees:0.#}°", r.Name.Contains("mount", StringComparison.OrdinalIgnoreCase) ? "A vehicle or mount hangs on it" : "");
            turns.Rows[i].Tag = r.Name;
        }
        Toggle(turns, "still");
        Ui.Tip(turns, "The bones this animation turns, the most first. Ticked: this costume's copy keeps that bone from turning, to the strength below (0 % = not at all). For a vehicle that banks with the rider: Ghost Rider's lean and pivot animations (add_leanleft, add_leanright, add_pivotleft, add_pivotright) bank g_mountattach, the bone his bike, or the model swapped onto it, hangs on.");
        t.Controls.Add(turns, 0, 5); t.SetColumnSpan(turns, 2);
        if (turnRows.Count == 0) { turns.Visible = false; var none = new Label { Text = "This animation turns no bones.", AutoSize = true, Tag = "subtle", Margin = new Padding(2, 4, 0, 0) }; t.Controls.Add(none, 0, 5); }
        stillSlider = new LightSlider { Label = "Turn Kept", Min = 0, Max = 1, Step = 0.05f, Mark = 0, Format = v => $"{v * 100:0} %", Home = () => 0, Value = Math.Clamp(stillStrength, 0, 1), Height = (int)(30 * s), Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        Ui.Tip(stillSlider, "How much of a ticked bone's turn stays: 0 % holds it still (a car stays level), 100 % turns it as the game does. Double-click: 0 %.");
        t.Controls.Add(stillSlider, 0, 6);
        t.Controls.Add(ok, 1, 6);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (Ui.TypingInSlider(this)) return;   // (Enter / Esc in a slider's typed value belong to that box: Kurt, 2026-11-17)
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
        };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 820, 760);
    }

    /// <summary>A click on a tick box flips it (the grid stays read-only: an edited check box cell shows its change only after
    /// the edit is committed, so a click seemed to do nothing; Kurt, 2026-11-17). Grayed rows don't change.</summary>
    /// <summary>The grayed rows a click doesn't change. (Not Row.ReadOnly: in a read-only grid every row reports read-only, so
    /// no box could be ticked at all; Kurt, 2026-11-20.)</summary>
    readonly HashSet<DataGridViewRow> locked = [];

    void Toggle(DataGridView g, string column)
    {
        g.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || g.Columns[e.ColumnIndex].Name != column || locked.Contains(g.Rows[e.RowIndex])) return;
            var c = g.Rows[e.RowIndex].Cells[column];
            c.Value = c.Value is not true;
        };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space || g.CurrentRow is not { } r || locked.Contains(r)) return;
            r.Cells[column].Value = r.Cells[column].Value is not true;
            e.Handled = true;
        };
    }

    static bool IsFinger(string n) => n.Contains("thumb", StringComparison.OrdinalIgnoreCase) || n.Contains("index", StringComparison.OrdinalIgnoreCase)
        || n.Contains("middle", StringComparison.OrdinalIgnoreCase) || n.Contains("ring", StringComparison.OrdinalIgnoreCase) || n.Contains("pinky", StringComparison.OrdinalIgnoreCase)
        || n.Contains("birdy", StringComparison.OrdinalIgnoreCase) || n.Contains("finger", StringComparison.OrdinalIgnoreCase);

    void TickWhere(Func<string, bool> pick)
    {
        foreach (DataGridViewRow r in bones.Rows) if (!locked.Contains(r) && r.Tag is string n && pick(n)) r.Cells["keep"].Value = true;
    }

    void Accept()
    {
        KeepLengths = bones.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["keep"].Value is true && r.Tag is string).Select(r => (string)r.Tag!).ToList();
        CurvesOff = curves.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["on"].Value is false && r.Tag is string).Select(r => (string)r.Tag!).ToList();
        StillBones = turns.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["still"].Value is true && r.Tag is string).Select(r => (string)r.Tag!).ToList();
        StillStrength = stillSlider.Value;
        DialogResult = DialogResult.OK;
        Close();
    }
}
