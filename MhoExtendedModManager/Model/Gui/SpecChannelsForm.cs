using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoMffImporter.Gui;

/// <summary>
/// The Materials tab's From Channels window (Kurt, 2026-10-04): an MHO spec map from separate gray images, one per channel,
/// or an RGB image for R, G and B plus a gray Reflectivity one, with the note on why (image editors show a PNG's alpha as
/// transparency and can change the colors under it when saving).
/// </summary>
sealed class SpecChannelsForm : Form
{
    readonly Label[] files = new Label[4];
    readonly Label rgbFile = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 7, 8, 0) };
    readonly CheckBox keep = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

    public string?[] Channels { get; } = new string?[4];
    public string? Rgb { get; private set; }
    /// <summary>Channels not picked keep the material's current MHO spec map's (when it has one), else the defaults.</summary>
    public bool KeepCurrent => keep.Checked;

    public SpecChannelsForm(string material, string? current)
    {
        Text = $"MHO Spec from Channels: {material}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = DeviceDpi / 96f;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var note = new Label { Text = SpecChannels.Note.Replace("\r\n\r\n", "\n\n"), AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(700 * s), 0), Margin = new Padding(0, 0, 0, 12) };
        t.Controls.Add(note, 0, 0); t.SetColumnSpan(note, 3);
        int row = 1;
        void Pick(string label, Label shown, Action<string?> set, string tip)
        {
            var name = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 12, 0) };
            shown.Text = "Not picked"; shown.AutoSize = true; shown.Tag = "subtle"; shown.Margin = new Padding(0, 7, 8, 0);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(Ui.FlatButton("Choose", () =>
            {
                using var d = new OpenFileDialog { Title = label, Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
                if (d.ShowDialog(this) != DialogResult.OK) return;
                set(d.FileName); shown.Text = Path.GetFileName(d.FileName);
            }, tip));
            buttons.Controls.Add(Ui.FlatButton("Clear", () => { set(null); shown.Text = "Not picked"; }, "Leave this one out."));
            t.Controls.Add(name, 0, row); t.Controls.Add(shown, 1, row); t.Controls.Add(buttons, 2, row);
            row++;
        }
        for (int c = 0; c < 4; c++)
        {
            int ci = c;
            files[c] = new Label();
            Pick(SpecChannels.Names[c], files[c], f => Channels[ci] = f, $"A gray image for {SpecChannels.Names[c]} (a color image counts by its brightness).");
        }
        Pick("Or RGB (R, G and B)", rgbFile, f => Rgb = f, "One image whose red, green and blue are Shine, Power and Skin Mask (a gray channel picked above wins over its channel).");
        keep.Text = current != null ? "Channels not picked keep the current MHO spec map's" : "Channels not picked: Shine 26, Power 49, no skin mask, no reflection";
        keep.Checked = current != null; keep.Enabled = current != null;
        t.Controls.Add(keep, 0, row); t.SetColumnSpan(keep, 3); row++;
        var ok = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
        ok.Controls.AddRange([
            Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, "Close without changing anything (Esc)."),
            Ui.AccentButton("Make the Map", Accept, "Combine the picked images into the MHO spec map (it goes into the mod; Undo takes it back)."),
        ]);
        t.Controls.Add(ok, 0, row); t.SetColumnSpan(ok, 3);
        Controls.Add(t);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
    }

    void Accept()
    {
        if (Channels.All(c => c == null) && Rgb == null) { Dialog.Show(this, "Pick at least one image.", "Nothing Picked"); return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}
