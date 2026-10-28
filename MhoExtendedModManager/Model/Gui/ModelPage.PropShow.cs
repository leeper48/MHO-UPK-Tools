using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Props view's left click (Kurt, 2026-10-27: "left click on prop in lower third displays it, right click applies to target
/// character"): the prop on its own over the preview, as its file has it (a game prop or summon as the game's model, written as
/// an FBX once; an MFF part as it would be added, sized to the target; an FBX as made). Back to the Costume (or a right-click
/// that adds it, another target, or leaving the Props view) shows the target again.
/// </summary>
sealed partial class ModelPage
{
    TableLayoutPanel? propShow;
    MeshViewer? propViewer;
    readonly Label propShowCap = new() { AutoSize = true, ForeColor = Ui.Text, Font = Ui.Bold(9f), Margin = new Padding(6, 7, 0, 0) };
    int propShowRun;

    bool PropShown => propShow?.Visible == true;

    void EnsurePropShow()
    {
        if (propShow != null) return;
        propViewer = new MeshViewer { Dock = DockStyle.Fill, Compact = true, Background = Color.FromArgb(28, 30, 38), Margin = new Padding(0) };
        var back = Ui.FlatButton("Back to the Costume", HidePropShow, "Shows the target costume again (Esc).");
        back.Margin = new Padding(0, 2, 6, 2);
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Color.FromArgb(22, 22, 26), Margin = new Padding(0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(propShowCap, 0, 0); bar.Controls.Add(back, 1, 0);
        propShow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.FromArgb(28, 30, 38), Margin = new Padding(0), Visible = false };
        propShow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        propShow.RowStyles.Add(new RowStyle(SizeType.AutoSize)); propShow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        propShow.Controls.Add(bar, 0, 0); propShow.Controls.Add(propViewer, 0, 1);
        Theme.ApplyTree(propShow, Palette.Dark);
        Ui.Restyle(propShow);
        // over the preview (its own child, on top): the preview keeps its state underneath
        preview.Controls.Add(propShow);
        propViewer.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { HidePropShow(); e.Handled = true; } };
    }

    void HidePropShow()
    {
        propShowRun++;
        if (propShow != null) propShow.Visible = false;
    }

    /// <summary>A prop of the Props view's list shown on its own over the preview.</summary>
    async void ShowPropModel(CharacterList.Item it)
    {
        EnsurePropShow();
        int run = ++propShowRun;
        string what = Plain(it.Title);
        propShowCap.Text = $"{what} · loading…";
        propViewer!.ShowMessage("Loading…");
        propShow!.Visible = true; propShow.BringToFront();
        // (the MFF part is sized to the target: its skeleton is read here)
        string? start = null, mapFile = null;
        MffModel? m = null;
        if (it.Key.StartsWith(PropMff))
        {
            string? charKey = (characters.SelectedItem as CharacterList.Item)?.Key;
            m = propModelKey == charKey ? propModel : chosenKey == charKey ? model : null;
            if (ChosenPackage is not CharacterList.Item pkg || m == null) { propViewer.ShowMessage("Pick the costume under Target first: an MFF part is shown sized to it."); propShowCap.Text = what; return; }
            start = StartPackageCore(pkg.Key);
            string mp = Path.Combine(host.WorkFolder, "maps", $"{m.Folder} on {Path.GetFileNameWithoutExtension(pkg.Key)}.json");
            mapFile = File.Exists(mp) ? mp : null;
        }
        string workDir = host.WorkFolder;
        using var busy = Busy.Begin($"Model: reading {what}");
        try
        {
            var (shown, maps, info) = await Task.Run(() =>
            {
                void Say(string line) => BeginInvoke(() => Log(line));
                string fbx;
                if (it.Key.StartsWith(PropGame) && int.TryParse(it.Key[PropGame.Length..], out int n) && n >= 0 && n < Props.Count)
                    fbx = GameSource.Export(Props[n].MeshPackage, Say, Props[n].Mesh);
                else if (it.Key.StartsWith(PropSummon)) fbx = GameSource.Export(it.Key[PropSummon.Length..], Say);
                else if (it.Key.StartsWith(PropFbx)) fbx = it.Key[PropFbx.Length..];
                else if (it.Key.StartsWith(PropMff) && m != null && start != null)
                {
                    var part = m.Parts.First(p => p.Name.Equals(it.Key[PropMff.Length..], StringComparison.OrdinalIgnoreCase));
                    fbx = MffPropExport.Write(m, part, MhoSkeleton.Load(start, null, body: true), mapFile != null ? BoneMapFile.Load(mapFile) : null,
                        Path.Combine(workDir, "props", FbxExport.SafeName(m.Folder)), Say).Fbx;
                }
                else throw new InvalidDataException("nothing to show");
                var d = FbxReimport.Display(fbx);
                var tex = d.ColorMaps.Select(f =>
                {
                    if (f == null || !File.Exists(f)) return null;
                    try { var (w, h, bgra) = ImagePixels.ReadBgra(f); return new MeshViewer.Tex(bgra, w, h); }
                    catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { return null; }
                }).ToArray();
                return (d, tex, $"{d.Pos.Length:N0} vertices, {d.Tris.Length / 3:N0} triangles · {fbx}");
            });
            if (run != propShowRun || IsDisposed) return;
            propViewer.ShowMesh(what, shown.Pos, shown.Normal, shown.Uv, shown.Tris, shown.TriSection, maps, info);
            propShowCap.Text = $"{what} · right-click it in the list to add it to {Plain(ChosenPackage?.Title ?? "the target")}";
            propViewer.Focus();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Assimp.AssimpException)
        {
            if (run != propShowRun || IsDisposed) return;
            propViewer.ShowMessage($"{what} can't be shown: {ex.Message}");
            Log($"{what}: {ex.Message}");
        }
    }

    /// <summary>Test hook: whether the prop view shows a model (and its caption).</summary>
    internal (bool Shown, bool HasMesh, string Caption) PropShowForTest => (PropShown, propViewer?.HasMesh == true, propShowCap.Text);
}
