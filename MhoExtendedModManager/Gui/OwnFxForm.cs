using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Powers tab → an Own Effects entry → Borrow Effects (a user, 2026-11-09: Ghost Rider's burning head, Firestar's flames,
/// Cable's eye glow on other characters): the character whose always-on effects to take (heroes, team-ups, NPCs and enemies
/// with an effect-only attachment: OwnFxBorrow.Sources), and whether they come on top of this character's own or replace them.
/// </summary>
sealed class OwnFxForm : Form
{
    readonly DropDown sourceBox = new() { Width = 460 };
    readonly Label what = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 8), MaximumSize = new Size(560, 0) };
    readonly RadioButton add = new() { Text = "Add: On Top of This Character's Own", AutoSize = true, Checked = true };
    readonly RadioButton replace = new() { Text = "Replace: Instead of This Character's Own", AutoSize = true };
    readonly List<OwnFxBorrow.Source> sources;

    public OwnFxBorrowEntry? Result { get; private set; }

    /// <param name="ownNames">This character's own effect attachments (Replace takes them off); empty: it has none.</param>
    public OwnFxForm(string targetName, IReadOnlyList<string> ownNames, OwnFxBorrowEntry? current)
    {
        Text = "Borrow Effects";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        // heroes first, then team-ups, then NPCs and enemies; by name; a character's own effects left out
        sources = OwnFxBorrow.Sources()
            .Where(s => !ownNames.Contains(s.Class, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s.Kind == "Hero" ? 0 : s.Kind == "Team-Up" ? 1 : 2).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = new Label { Text = "Borrow Effects for " + targetName, AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) };
        t.Controls.Add(head, 0, 0); t.SetColumnSpan(head, 2);
        var hint = new Label
        {
            Text = "Another character's always-on effects (Ghost Rider's burning head, Firestar's flames, Cable's eye glow) on this one, at the same places on its body. Only this package changes; the other character stays as it is.",
            AutoSize = true, Tag = "subtle", MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10),
        };
        t.Controls.Add(hint, 0, 1); t.SetColumnSpan(hint, 2);
        t.Controls.Add(new Label { Text = "Character", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 2);
        t.Controls.Add(sourceBox, 1, 2);
        var modes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        modes.Controls.AddRange([what, add, replace]);
        t.Controls.Add(modes, 0, 3); t.SetColumnSpan(modes, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Cancel", () => DialogResult = DialogResult.Cancel, tip: "Change nothing (Esc)."),
            Ui.AccentButton("Borrow", Choose, tip: "This character takes the chosen effects; the preview shows them, Save puts them into the mod (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 4); t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        sourceBox.Format = o => o is OwnFxBorrow.Source s ? $"{s.Name}  ·  {s.Kind}" : o.ToString() ?? "";
        sourceBox.Items.AddRange([.. sources]);
        sourceBox.SelectedIndexChanged += (_, _) => ShowWhat();
        replace.Enabled = ownNames.Count > 0;
        if (ownNames.Count == 0) replace.Text = "Replace: Instead of This Character's Own (It Has None)";
        Ui.Tip(sourceBox, "The character whose always-on effects to borrow: heroes first, then team-ups, then NPCs and enemies.");
        Ui.Tip(add, "The borrowed effects play together with this character's own (if it has any).");
        Ui.Tip(replace, "This character's own always-on effects are taken off and the borrowed ones play instead (Human Torch's flames in place of Ghost Rider's). Its weapons and other attachments stay.");
        if (current != null) replace.Checked = current.Replace && replace.Enabled;
        int at = current == null ? -1 : sources.FindIndex(s => s.Class.Equals(current.Class, StringComparison.OrdinalIgnoreCase) && s.File.Equals(current.Donor, StringComparison.OrdinalIgnoreCase));
        Shown += (_, _) => { if (sources.Count > 0) sourceBox.SelectedIndex = at >= 0 ? at : 0; };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter) { e.Handled = true; Choose(); }
        };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 620, 330);
    }

    void ShowWhat()
    {
        if (sourceBox.SelectedItem is not OwnFxBorrow.Source s) { what.Text = ""; return; }
        string where = s.Sockets.Replace("socket_", "").Replace("l_", "left ").Replace("r_", "right ").Replace('_', ' ');
        what.Text = $"{s.Templates} effect{(s.Templates == 1 ? "" : "s")}, at: {where}.";
    }

    void Choose()
    {
        if (sourceBox.SelectedItem is not OwnFxBorrow.Source s) return;
        Result = new OwnFxBorrowEntry { Donor = s.File, Class = s.Class, Name = s.Name, Replace = replace.Checked && replace.Enabled };
        DialogResult = DialogResult.OK;
    }
}
