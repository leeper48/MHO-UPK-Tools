namespace MhoExtendedModManager.Gui;

/// <summary>
/// In This Animation (Kurt, 2026-10-23): an added prop moved along and turned about the character's axes in one animation
/// only (PropAnimFit, baked by PropHold with Pivot on the Hand), with Copy From another animation's and Reset. The
/// Animations tab has one for the row selected; the Powers tab one for a borrowed power's animation playing, when it throws
/// the prop the costume holds.
/// </summary>
sealed class AnimFitBlock
{
    readonly LightSlider x, y, z, tx, ty, tz;
    readonly Label head = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 2) };
    readonly Label note = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 2, 0, 4), MaximumSize = new Size(420, 0) };
    readonly DropDown copy = new() { ShowsSelection = false, Placeholder = "Copy From" };
    readonly Control tools;
    readonly Func<(AddedPropEntry? Entry, string? Slot, string Why)> target;
    readonly Action<AddedPropEntry, string, PropAnimFit?> changed;
    bool showing;

    public FlowLayoutPanel Root { get; } = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };

    /// <param name="target">The prop, the animation (its slot name, shown in the heading) and why it can't be adjusted there
    /// ("" when it can).</param>
    /// <param name="changed">The prop's adjustment in that animation set (null: none), to be built again.</param>
    /// <summary>Lit: the preview's gizmo moves and turns this animation's adjustment, not the prop's fit (Kurt, 2026-10-30:
    /// choose which the gizmo works on). Only with a gizmo (the Powers tab's).</summary>
    public bool GizmoHere { get; set; }
    readonly Button? gizmoHere;
    /// <summary>The sliders' two-column table (twoColumns), or null.</summary>
    readonly Control? sliderGrid;
    /// <summary>The two-column block opened (its heading clicked).</summary>
    bool open;

    public AnimFitBlock(float scale, string headTip, Func<(AddedPropEntry? Entry, string? Slot, string Why)> target, Action<AddedPropEntry, string, PropAnimFit?> changed, Func<Control[], Control> toolbar, bool gizmo = false, bool twoColumns = false)
    {
        this.target = target; this.changed = changed;
        string Units(float v) => $"{v:+0;-0;0}";
        string Deg(float v) => $"{v:+0;-0;0}°";
        const string only = " In this animation only, on top of the prop's fit; baked into it on Save. Double-click: back to 0.";
        LightSlider Slider(string label, float min, float max, Func<float, string> format, string tip)
        {
            var sl = new LightSlider { Label = label, Min = min, Max = max, Step = 1, Mark = 0, Home = () => 0, Format = format, Width = (int)(320 * scale), Height = (int)(30 * scale), Margin = new Padding(0, 2, 0, 0) };
            Ui.Tip(sl, tip + only);
            sl.Committed += Committed;
            return sl;
        }
        x = Slider("Move X", -60, 60, Units, "Moves the prop along the global X axis (red on the axis gizmo: the way the character faces), in every frame.");
        y = Slider("Move Y", -60, 60, Units, "Moves the prop along the global Y axis (green on the axis gizmo), in every frame.");
        z = Slider("Move Z", -60, 60, Units, "Moves the prop along the global Z axis (blue on the axis gizmo: up), in every frame.");
        tx = Slider("Turn X", -180, 180, Deg, "Turns the prop about the global X axis (red), around the point it hangs on (Look ▾ → Prop Pivots), in every frame.");
        ty = Slider("Turn Y", -180, 180, Deg, "Turns the prop about the global Y axis (green), around the point it hangs on, in every frame.");
        tz = Slider("Turn Z", -180, 180, Deg, "Turns the prop about the global Z axis (blue: up), around the point it hangs on, in every frame.");
        head.Font = Ui.Bold(8.5f);
        Ui.Tip(head, headTip);
        copy.Width = (int)(150 * scale);
        copy.Opening += (_, _) => FillCopy();
        copy.SelectedIndexChanged += (_, _) => { if (copy.SelectedItem is string from) CopyFrom(from); };
        Ui.Tip(copy, "Start from another animation's adjustment of this prop (a copy: change it from there).");
        var reset = Ui.FlatButton("Reset", Reset, "This animation's adjustment back to none: the prop as its fit holds it.");
        if (gizmo)
        {
            gizmoHere = Ui.FlatButton("Gizmo: This Animation Only", () => GizmoTargetChanged?.Invoke(),
                "The gizmo in the preview on this animation's adjustment (these sliders): its arrows and rings move and turn the prop in this animation only. Lit when it's on; click again to hide the gizmo. Gizmo: All Animations (under Prop Fit) is the other choice.");
            Icons.Make(gizmoHere, "Gizmo: This Animation Only", Icons.Gizmo, scale);
            Ui.Lit(gizmoHere, false);
        }
        tools = toolbar(gizmoHere != null ? [copy, reset, gizmoHere] : [copy, reset]);
        if (twoColumns)
        {
            // Move | Turn side by side (the Model tab's column, Kurt 2026-10-30: the panel under it kept its room)
            var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0), Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            LightSlider[] l = [x, y, z], r = [tx, ty, tz];
            for (int k = 0; k < 3; k++)
            {
                l[k].Dock = DockStyle.Fill; l[k].Margin = new Padding(0, k == 0 ? 0 : 2, 8, 0); grid.Controls.Add(l[k], 0, k);
                r[k].Dock = DockStyle.Fill; r[k].Margin = new Padding(8, k == 0 ? 0 : 2, 0, 0); grid.Controls.Add(r[k], 1, k);
            }
            sliderGrid = grid;
            // folds (the Model tab's column has no room for the sliders always): the heading opens it, and so does Gizmo:
            // This Animation Only; folded, the heading shows the values
            head.Cursor = Cursors.Hand;
            head.Click += (_, _) => { open = !open; Show(); };
            Root.Controls.AddRange([head, note, grid, tools]);
        }
        else Root.Controls.AddRange([head, note, x, y, z, tx, ty, tz, tools]);
    }

    /// <summary>Gizmo: This Animation Only clicked (the view decides, with its Gizmo: All Animations).</summary>
    public event Action? GizmoTargetChanged;

    /// <summary>Gizmo: This Animation Only lit or not.</summary>
    public void LightGizmo(bool on) { if (gizmoHere != null) Ui.Lit(gizmoHere, on); if (sliderGrid != null) Show(); }

    /// <summary>The prop and animation the sliders adjust (null when they can't).</summary>
    public (AddedPropEntry Entry, string Slot)? Target => target() is var (e, slot, why) && e != null && slot != null && why.Length == 0 ? (e, slot) : null;

    /// <summary>The adjustment as the sliders have it.</summary>
    public PropAnimFit Current => new() { Slot = Target?.Slot ?? "", X = x.Value, Y = y.Value, Z = z.Value, TurnX = tx.Value, TurnY = ty.Value, TurnZ = tz.Value };

    /// <summary>The sliders set (a gizmo drag), without putting it in yet.</summary>
    public void SetLive(PropAnimFit a)
    {
        showing = true;
        try { x.Value = a.X; y.Value = a.Y; z.Value = a.Z; tx.Value = a.TurnX; ty.Value = a.TurnY; tz.Value = a.TurnZ; }
        finally { showing = false; }
    }

    /// <summary>The sliders' adjustment put in (letting go of the gizmo), as letting go of a slider does.</summary>
    public void Commit() => Committed();

    public static PropAnimFit? Of(AddedPropEntry e, string? slot) => slot == null ? null : e.Anims?.FirstOrDefault(a => a.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));

    /// <summary>The prop and animation as they are now, into the heading, the note and the sliders.</summary>
    public void Show()
    {
        var (e, slot, why) = target();
        bool can = e != null && slot != null && why.Length == 0;
        head.Visible = e != null;
        head.Text = slot != null ? "IN THIS ANIMATION: " + slot.ToUpperInvariant() : "IN THIS ANIMATION";
        foreach (var c in new Control[] { x, y, z, tx, ty, tz, tools }) c.Visible = can;
        if (sliderGrid != null)
        {
            bool shown = can && (open || GizmoHere);
            sliderGrid.Visible = shown;
            if (can && !shown && Of(e!, slot) is var f)
                head.Text = (f is { IsZero: false } ? $"▸ IN THIS ANIMATION: {slot!.ToUpperInvariant()}  ·  Move {f.X:0}, {f.Y:0}, {f.Z:0}  ·  Turn {f.TurnX:0}°, {f.TurnY:0}°, {f.TurnZ:0}°" : $"▸ IN THIS ANIMATION: {slot!.ToUpperInvariant()}  ·  none");
            else if (can) head.Text = "▾ " + head.Text;
        }
        note.Text = e == null ? "" : why;
        note.Visible = e != null && why.Length > 0;
        if (!can) return;
        var a = Of(e!, slot) ?? new PropAnimFit();
        showing = true;
        try
        {
            // (the character's axes: the values as stored)
            x.Value = a.X; y.Value = a.Y; z.Value = a.Z;
            tx.Value = a.TurnX; ty.Value = a.TurnY; tz.Value = a.TurnZ;
        }
        finally { showing = false; }
    }

    void Committed()
    {
        if (showing) return;
        var (e, slot, why) = target();
        if (e == null || slot == null || why.Length > 0) return;
        changed(e, slot, new PropAnimFit { Slot = slot, X = x.Value, Y = y.Value, Z = z.Value, TurnX = tx.Value, TurnY = ty.Value, TurnZ = tz.Value });
    }

    void FillCopy()
    {
        copy.Items.Clear();
        var (e, slot, _) = target();
        if (e == null) return;
        copy.Items.AddRange([.. (e.Anims ?? []).Where(a => !a.IsZero && !a.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase)).Select(a => (object)a.Slot)]);
    }

    void CopyFrom(string from)
    {
        var (e, slot, why) = target();
        if (e == null || slot == null || why.Length > 0 || Of(e, from) is not { } src) return;
        changed(e, slot, new PropAnimFit { Slot = slot, X = src.X, Y = src.Y, Z = src.Z, TurnX = src.TurnX, TurnY = src.TurnY, TurnZ = src.TurnZ });
        Show();
    }

    void Reset()
    {
        var (e, slot, why) = target();
        if (e == null || slot == null || why.Length > 0) return;
        changed(e, slot, null);
        Show();
    }

    /// <summary>Tests: what the block shows.</summary>
    public string State() => $"note '{note.Text}', head '{head.Text}', sliders {(x.Visible ? "shown" : "hidden")}";
}
