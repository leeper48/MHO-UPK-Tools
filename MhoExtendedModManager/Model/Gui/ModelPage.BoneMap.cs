using AnimExportCli.Animation;
using MhoExtendedModManager;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>The Model tab's Bone Map: pairs, picking bones, Smooth Weights.</summary>
sealed partial class ModelPage
{
    // --- bone map editor ----------------------------------------------------------------------------------------------------------
    /// <summary>This character on this base hero's map file (data\maps); null without a source and a base hero. An FBX source's
    /// map holds only its weight smoothing (Kurt, 2026-10-04: Smooth Weights was grayed out for FBX sources): its bones are
    /// its own, so nothing is paired.</summary>
    string? MapPath() => ChosenPackage is not CharacterList.Item pkg ? null
        : model != null ? Path.Combine(host.WorkFolder, "maps", $"{model.Folder} on {Path.GetFileNameWithoutExtension(pkg.Key)}.json")
        : sourceFbx != null ? Path.Combine(host.WorkFolder, "maps", $"{ImportBuild.SourceName(sourceFbx)} on {Path.GetFileNameWithoutExtension(pkg.Key)}.json")
        : null;

    /// <summary>The map the preview used: chains first, then the bones (lines the chains set are shown, not editable).</summary>
    bool fillingMap;

    void FillMap()
    {
        int keepRow = mapGrid.CurrentCell?.RowIndex ?? -1;
        fillingMap = true;
        try
        {
        mapGrid.Rows.Clear();
        bool edited = MapPath() is string mp && File.Exists(mp);
        modelTabs.SetTitle(1, edited ? "Bone Map ✎" : "Bone Map");
        mapReset.Enabled = edited;
        if (shownMap == null) return;
        string f = mapFilter.Text.Trim();
        bool Show(string a, string? b) => f.Length == 0 || a.Contains(f, StringComparison.OrdinalIgnoreCase) || (b?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
        foreach (var c in shownMap.Chains)
            if (Show(c.Mff, c.Mho)) { int i = mapGrid.Rows.Add("Chain", c.Mff, c.Mho ?? "(not paired)", (c.Fit ?? "") + SmoothNote(c.Mho)); mapGrid.Rows[i].Tag = c; }
        foreach (var b in shownMap.Bones)
            if (Show(b.Was ?? b.Mff, b.Mho) || (b.Was != null && Show(b.Mff, null)))
            {
                // a renamed bone (Mixamo, or a skeleton guessed from its shape) shows its own name from the FBX
                int i = mapGrid.Rows.Add("Bone", b.Was ?? b.Mff, b.Mho ?? "(nearest mapped parent)", b.How + SmoothNote(RowTarget(b)));
                mapGrid.Rows[i].Tag = b;
                if (b.How == "chain") mapGrid.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle;
                else if (b.How.StartsWith("guessed", StringComparison.Ordinal)) mapGrid.Rows[i].DefaultCellStyle.ForeColor = Ui.OverrideAmber;
            }
        if (keepRow >= 0 && keepRow < mapGrid.Rows.Count) mapGrid.CurrentCell = mapGrid.Rows[keepRow].Cells["mff"];
        }
        finally { fillingMap = false; }
        MapSelectionChanged();
    }

    /// <summary>The MHO bone a bone row's weights go to: its own target, else the parent its weights fall to.</summary>
    static string? RowTarget(BoneMapFile.BoneEntry b) =>
        b.Mho ?? (b.How.StartsWith("parent → ", StringComparison.Ordinal) ? b.How["parent → ".Length..] : null);

    /// <summary>" · smoothed ×2" when the bone's weights are smoothed in the map.</summary>
    string SmoothNote(string? bone) =>
        bone != null && shownMap?.Smooth.FirstOrDefault(s => s.Bone.Equals(bone, StringComparison.OrdinalIgnoreCase)) is { Passes: > 0 } e ? $" · smoothed ×{e.Passes}" : "";

    /// <summary>The MHO bones a map row stands for: a bone row its target; a chain row its MHO chain (the root and every bone
    /// under it).</summary>
    List<string> RowBones(int row)
    {
        if (row < 0 || row >= mapGrid.Rows.Count) return [];
        switch (mapGrid.Rows[row].Tag)
        {
            case BoneMapFile.BoneEntry b: return RowTarget(b) is string t ? [t] : [];
            case BoneMapFile.ChainEntry { Mho: string root }:
            {
                int ri = mhoBones.FindIndex(n => n.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (ri < 0) return [root];
                var list = new List<string>();
                for (int i = 0; i < mhoBones.Count; i++)
                    for (int k = i, g = 0; k >= 0 && g < 256 && k < mhoParents.Count; k = mhoParents[k], g++)
                        if (k == ri) { list.Add(mhoBones[i]); break; }
                return list;
            }
            default: return [];
        }
    }

    /// <summary>The selected row's bones are drawn in orange on the skeleton overlay; Smooth Weights acts on them.</summary>
    /// <summary>The bones the Bone Map tab acts on: the selected row's, else a bone picked in the view without a row.</summary>
    List<string> SelectedBones() => mapGrid.CurrentCell != null ? RowBones(mapGrid.CurrentCell.RowIndex) : pickedBone != null ? [pickedBone] : [];

    void MapSelectionChanged()
    {
        if (mapGrid.CurrentCell != null) pickedBone = null;
        var bones = SelectedBones();
        preview.Highlight(bones);
        mapSmooth.Enabled = bones.Count > 0 && MapPath() != null;
    }

    /// <summary>
    /// A bone Ctrl+clicked in the view (0.13.3): the Bone Map tab opens on the row for it: the MFF bone that drives it, else
    /// one whose weights fall to it ("parent → it"), else the chain it belongs to. The filter is cleared when it hides it.
    /// </summary>
    void SelectBoneRow(string bone)
    {
        modelTabs.Select(1);
        int Find()
        {
            int fallback = -1;
            for (int i = 0; i < mapGrid.Rows.Count; i++)
            {
                if (mapGrid.Rows[i].Tag is BoneMapFile.BoneEntry b && RowTarget(b) is string t && t.Equals(bone, StringComparison.OrdinalIgnoreCase))
                {
                    if (b.Mho != null && b.How != "chain") return i;   // the MFF bone that drives it
                    if (fallback < 0) fallback = i;
                }
                else if (fallback < 0 && mapGrid.Rows[i].Tag is BoneMapFile.ChainEntry && RowBones(i).Contains(bone, StringComparer.OrdinalIgnoreCase)) fallback = i;
            }
            return fallback;
        }
        int row = Find();
        if (row < 0 && mapFilter.Text.Length > 0) { mapFilter.Text = ""; row = Find(); }
        if (row >= 0)
        {
            mapGrid.CurrentCell = mapGrid.Rows[row].Cells["mff"];
            mapGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, row - 3);
            Log($"Picked {bone}: {mapGrid.Rows[row].Cells["mff"].Value} → {mapGrid.Rows[row].Cells["mho"].Value}");
        }
        else
        {
            // no row: the bone itself is what Weights shows and Smooth Weights smooths
            mapGrid.CurrentCell = null;
            pickedBone = bone;
            MapSelectionChanged();
            Log($"Picked {bone}: no MFF bone or chain maps to it directly (its weights come from the retarget's blending); Weights and Smooth Weights act on it.");
        }
    }

    /// <summary>One more smoothing click on the selected row's bones, saved in the map (made from the automatic one if
    /// there's none yet); the preview follows.</summary>
    void SmoothSelected()
    {
        if (shownMap == null || MapPath() is not string path) return;
        var bones = SelectedBones();
        if (bones.Count == 0) return;
        foreach (var bone in bones)
        {
            var e = shownMap.Smooth.FirstOrDefault(s => s.Bone.Equals(bone, StringComparison.OrdinalIgnoreCase));
            if (e == null) shownMap.Smooth.Add(e = new BoneMapFile.SmoothEntry { Bone = bone });
            e.Passes++;
        }
        shownMap.Save(path);
        Log($"Smooth weights: {string.Join(", ", bones.Take(6))}{(bones.Count > 6 ? $" and {bones.Count - 6} more" : "")} (saved: {path})");
        FillMap(); SchedulePreview();
    }
}
