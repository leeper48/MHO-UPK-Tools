using AnimExportCli.Animation;
using MhoExtendedModManager;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>The Model tab's Undo / Redo.</summary>
sealed partial class ModelPage
{
    // --- undo / redo (0.12.0, Kurt) ---------------------------------------------------------------------------------------------
    /// <summary>What Undo / Redo restores: the ticked parts, Smooth, the material and the bone map file (its text; null = none).</summary>
    sealed record UiState(bool[] Parts, bool Subdivide, int Material, string? Map, int Cape = 0, int Hair = 0, string Edits = "", string? Overrides = null)
    {
        public bool Same(UiState o) => Parts.SequenceEqual(o.Parts) && Subdivide == o.Subdivide && Material == o.Material && Map == o.Map && Cape == o.Cape && Hair == o.Hair && Edits == o.Edits && Overrides == o.Overrides;
    }
    readonly Stack<UiState> undo = new(), redo = new();
    UiState? committed;
    string? historyKey;
    bool restoring;

    UiState Capture() => new(parts.Rows.Cast<DataGridViewRow>().Select(r => r.Cells["use"].Value is true).ToArray(), smooth.Checked,
        Math.Max(0, material.SelectedIndex), MapPath() is string p && File.Exists(p) ? File.ReadAllText(p) : null, Math.Max(0, capeBox.SelectedIndex), Math.Max(0, hairBox.SelectedIndex), EnsureEdits().Serialize(),
        OverridesFile() is string ov ? File.ReadAllText(ov) : null);

    /// <summary>Called as the preview rebuilds (after the short delay, so quick clicks are one step): a change since the
    /// last state becomes an undo step. The history starts over for another character or base hero.</summary>
    void Remember(string package)
    {
        string key = (chosenKey ?? "") + "|" + package;
        var now = Capture();
        if (key != historyKey) { historyKey = key; undo.Clear(); redo.Clear(); committed = now; }
        else if (committed != null && !now.Same(committed) && !restoring) { undo.Push(committed); redo.Clear(); committed = now; }
        else committed = now;
        restoring = false;
        undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0;
        SaveState();
    }

    void Undo() { if (undo.Count > 0) { redo.Push(Capture()); Restore(undo.Pop(), "Undo"); } }
    void Redo() { if (redo.Count > 0) { undo.Push(Capture()); Restore(redo.Pop(), "Redo"); } }

    void Restore(UiState s, string what)
    {
        restoring = true; committed = s;
        for (int i = 0; i < parts.Rows.Count && i < s.Parts.Length; i++)
            if ((parts.Rows[i].Cells["use"].Value is true) != s.Parts[i]) parts.Rows[i].Cells["use"].Value = s.Parts[i];
        if (smooth.Checked != s.Subdivide) smooth.Checked = s.Subdivide;
        if (material.SelectedIndex != s.Material) material.SelectedIndex = s.Material;
        if (capeBox.SelectedIndex != s.Cape) capeBox.SelectedIndex = s.Cape;
        if (hairBox.SelectedIndex != s.Hair) hairBox.SelectedIndex = s.Hair;
        if (MapPath() is string path)
        {
            if (s.Map == null) { if (File.Exists(path)) File.Delete(path); }
            else { Protected.CheckWrite(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, s.Map); }
            if (shownMap != null && s.Map != null) shownMap = BoneMapFile.Load(path);
        }
        if (EnsureEdits().Serialize() != s.Edits) { edits = AnimEdits.Parse(s.Edits); SaveEdits(); }
        if (OverridesPath() is string op)
        {
            // the Materials tab's overrides (their image files stay in the Model folder)
            if (s.Overrides == null) { if (File.Exists(op)) File.Delete(op); }
            else { Protected.CheckWrite(op); Directory.CreateDirectory(Path.GetDirectoryName(op)!); File.WriteAllText(op, s.Overrides); }
        }
        undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0;
        Log(what + ": back to the earlier parts / Smooth / material / bone map / FBX edits.");
        FillMap(); SchedulePreview();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool typing = ActiveControl is TextBox || (ActiveControl is ContainerControl c && c.ActiveControl is TextBox);
        if (keyData == Keys.F11) { preview.ToggleFull(); return true; }
        if (!typing && keyData == Keys.Escape && preview.PausePlayback()) return true;
        if (!typing && keyData == Keys.P && preview.TogglePlayback()) return true;   // (P: play / pause in every 3D view)
        if (!typing && keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
        if (!typing && (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z))) { Redo(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Opens the list of MHO bones for a row; a pick saves the map and redraws the preview.</summary>
    void PickMapTarget(int row)
    {
        if (model == null) { Log("An FBX's bones are its own (nothing is paired): move or rename them in Blender. Smooth Weights works here."); return; }
        if (shownMap == null || MapPath() is not string path) return;
        var tag = mapGrid.Rows[row].Tag;
        if (tag is BoneMapFile.BoneEntry { How: "chain" }) { Log("That bone is set by its chain: change the chain's row instead."); return; }
        bool chain = tag is BoneMapFile.ChainEntry;
        var names = new List<string> { chain ? "(not paired)" : "(nearest mapped parent)" };
        names.AddRange(mhoBones.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        string? current = tag is BoneMapFile.ChainEntry ce ? ce.Mho : (tag as BoneMapFile.BoneEntry)?.Mho;
        int sel = current == null ? 0 : names.FindIndex(n => n.Equals(current, StringComparison.OrdinalIgnoreCase));
        var cell = mapGrid.GetCellDisplayRectangle(mapGrid.Columns["mho"]!.Index, row, false);
        DropList.Show(mapGrid, mapGrid.RectangleToScreen(cell), names, sel, pick =>
        {
            string? to = pick <= 0 ? null : names[pick];
            if (tag is BoneMapFile.ChainEntry c) { c.Mho = to; c.Fit = "edited"; }
            else if (tag is BoneMapFile.BoneEntry b) { b.Mho = to; b.How = "edited"; }
            shownMap.Save(path);
            Log($"Bone map: {(chain ? "chain " : "")}{mapGrid.Rows[row].Cells["mff"].Value} → {to ?? (chain ? "not paired" : "nearest mapped parent")} (saved: {path})");
            FillMap(); SchedulePreview();
        });
    }
}
