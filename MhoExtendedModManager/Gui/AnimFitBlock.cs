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
    public AnimFitBlock(float scale, string headTip, Func<(AddedPropEntry? Entry, string? Slot, string Why)> target, Action<AddedPropEntry, string, PropAnimFit?> changed, Func<Control[], Control> toolbar)
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
        tools = toolbar([copy, reset]);
        Root.Controls.AddRange([head, note, x, y, z, tx, ty, tz, tools]);
    }

    public static PropAnimFit? Of(AddedPropEntry e, string? slot) => slot == null ? null : e.Anims?.FirstOrDefault(a => a.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));

    /// <summary>The prop and animation as they are now, into the heading, the note and the sliders.</summary>
    public void Show()
    {
        var (e, slot, why) = target();
        bool can = e != null && slot != null && why.Length == 0;
        head.Visible = e != null;
        head.Text = slot != null ? "IN THIS ANIMATION: " + slot.ToUpperInvariant() : "IN THIS ANIMATION";
        foreach (var c in new Control[] { x, y, z, tx, ty, tz, tools }) c.Visible = can;
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
