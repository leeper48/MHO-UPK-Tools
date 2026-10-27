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
    sealed record UiState(bool[] Parts, bool Subdivide, int Material, string? Map, string Rigs = "", string Edits = "", string? Overrides = null, float Size = 1, string? Paint = null)
    {
        /// <summary>What changed to reach this state (the History list's line).</summary>
        public string Label { get; init; } = "";
        public bool Same(UiState o) => Parts.SequenceEqual(o.Parts) && Subdivide == o.Subdivide && Material == o.Material && Map == o.Map && Rigs == o.Rigs && Edits == o.Edits && Overrides == o.Overrides && Math.Abs(Size - o.Size) < 1e-4 && Paint == o.Paint;
    }
    readonly Stack<UiState> undo = new(), redo = new();
    /// <summary>
    /// Prop steps (Kurt, 2026-10-27: a removed prop didn't come back with Ctrl+Z): the props before each add, remove, tick,
    /// fit, bone or model change (the editor's, IModelHost.PropState). Kept apart from the steps above, whose history starts over
    /// with another source; Undo / Redo take whichever step is the most recent.
    /// </summary>
    readonly Stack<(DateTime At, string Label, string State)> propUndo = new(), propRedo = new();
    /// <summary>When each step above was made (Undo takes the newer of the two kinds).</summary>
    readonly Stack<DateTime> undoAt = new(), redoAt = new();

    /// <summary>The props before a change (<see cref="PropDone"/> keeps it as a step when the change happened).</summary>
    string PropBefore() => host.PropState();

    void PropDone(string before, string label)
    {
        if (host.PropState() == before) return;
        propUndo.Push((DateTime.UtcNow, label, before));
        propRedo.Clear();
        undoButton.Enabled = true; redoButton.Enabled = redo.Count > 0;
    }

    /// <summary>A prop step undone or redone: the props back to that state, the lists and the target read again.</summary>
    async Task PropRestore(Stack<(DateTime At, string Label, string State)> from, Stack<(DateTime At, string Label, string State)> to, string what)
    {
        var step = from.Pop();
        string now = host.PropState();
        to.Push((DateTime.UtcNow, step.Label, now));
        string? pickedClass = addedPick?.Class;
        if (!await host.SetPropState(step.State) || IsDisposed) { Log($"{what}: the props couldn't be put back."); return; }
        Log($"{what}: {step.Label}.");
        if (ChosenPackage is CharacterList.Item pkg)
        {
            var again = pickedClass == null ? null : host.AddedProps(pkg.Key).FirstOrDefault(a => a.Class.Equals(pickedClass, StringComparison.OrdinalIgnoreCase));
            if (again == null && addedPick != null) { addedPick = null; MhoSkeleton.Choose(pkg.Key, null); PropTicks(changed: true); SourceForTarget(); SaveState(); }
            await AddedPropsChanged(pkg.Key, again);
        }
        undoButton.Enabled = undo.Count > 0 || propUndo.Count > 0; redoButton.Enabled = redo.Count > 0 || propRedo.Count > 0;
    }
    UiState? committed;
    string? historyKey;
    bool restoring;

    UiState Capture() => new(parts.Rows.Cast<DataGridViewRow>().Select(r => r.Cells["use"].Value is true).ToArray(), smooth.Checked,
        Math.Max(0, material.SelectedIndex), MapPath() is string p && File.Exists(p) ? File.ReadAllText(p) : null, RigsText(), EnsureEdits().Serialize(),
        OverridesFile() is string ov ? File.ReadAllText(ov) : null, sizeSlider.Value, PaintText());

    /// <summary>Called as the preview rebuilds (after the short delay, so quick clicks are one step): a change since the
    /// last state becomes an undo step. The history starts over for another character or base hero.</summary>
    void Remember(string package)
    {
        string key = (chosenKey ?? "") + "|" + package;
        var now = Capture();
        if (key != historyKey) { historyKey = key; undo.Clear(); redo.Clear(); undoAt.Clear(); redoAt.Clear(); committed = now; }
        else if (committed != null && !now.Same(committed) && !restoring) { undo.Push(committed); undoAt.Push(DateTime.UtcNow); redo.Clear(); redoAt.Clear(); committed = now with { Label = Describe(committed, now) }; }
        else if (committed == null || !committed.Same(now)) committed = now;   // (the same state keeps its History name)
        restoring = false;
        undoButton.Enabled = undo.Count > 0 || propUndo.Count > 0; redoButton.Enabled = redo.Count > 0 || propRedo.Count > 0;
        SaveState();
    }

    void Undo()
    {
        // (the newer of a prop step and a step of the tab's own)
        if (propUndo.Count > 0 && (undo.Count == 0 || undoAt.Count == 0 || propUndo.Peek().At >= undoAt.Peek())) { _ = PropRestore(propUndo, propRedo, "Undo"); return; }
        if (undo.Count > 0) { if (undoAt.Count > 0) undoAt.Pop(); redoAt.Push(DateTime.UtcNow); redo.Push(Current()); Restore(undo.Pop(), "Undo"); }
    }
    void Redo()
    {
        if (propRedo.Count > 0 && (redo.Count == 0 || redoAt.Count == 0 || propRedo.Peek().At >= redoAt.Peek())) { _ = PropRestore(propRedo, propUndo, "Redo"); return; }
        if (redo.Count > 0) { if (redoAt.Count > 0) redoAt.Pop(); undoAt.Push(DateTime.UtcNow); undo.Push(Current()); Restore(redo.Pop(), "Redo"); }
    }

    /// <summary>The state now, with the name of the step that reached it (the History list keeps it).</summary>
    UiState Current() { var now = Capture(); return committed != null && committed.Same(now) ? committed : now with { Label = committed != null ? Describe(committed, now) : "" }; }

    /// <summary>The step from <paramref name="a"/> to <paramref name="b"/> in words (History).</summary>
    static string Describe(UiState a, UiState b)
    {
        var parts = new List<string>();
        if (!a.Parts.SequenceEqual(b.Parts)) parts.Add("Parts");
        if (a.Subdivide != b.Subdivide) parts.Add("Smooth (Subdivide)");
        if (a.Material != b.Material) parts.Add("Material");
        if (a.Map != b.Map) parts.Add("Bone Map");
        if (a.Rigs != b.Rigs) parts.Add("Rigs");
        if (a.Edits != b.Edits) parts.Add("Blender Edits");
        if (a.Overrides != b.Overrides) parts.Add("Materials Tab");
        if (Math.Abs(a.Size - b.Size) > 1e-4) parts.Add("Size");
        if (a.Paint != b.Paint) parts.Add("Weight Paint");
        return parts.Count == 0 ? "Change" : string.Join(", ", parts);
    }

    /// <summary>History: every step (the oldest first, the one now marked); a pick undoes or redoes to it (Kurt, 2026-10-25).</summary>
    void ShowHistory()
    {
        var steps = undo.Reverse().ToList();
        int now = steps.Count;
        steps.Add(Current());
        steps.AddRange(redo);
        var texts = steps.Select((x, i) => i == 0 ? "Start" : x.Label.Length > 0 ? x.Label : "Change").Select((t, i) => (i == now ? "▶ " : "    ") + t + (i > now ? " (undone)" : "")).ToList();
        DropList.Show(historyButton, historyButton.RectangleToScreen(historyButton.ClientRectangle), texts, now, pick => JumpTo(pick), i => i > now ? Ui.Subtle : i == now ? Ui.Accent : null);
    }

    /// <summary>History's step <paramref name="pick"/> (0 = the start): undone or redone to it.</summary>
    void JumpTo(int pick)
    {
        int now = undo.Count;
        for (int k = now; k > pick && undo.Count > 0; k--) Undo();
        for (int k = now; k < pick && redo.Count > 0; k++) Redo();
    }

    /// <summary>Test: the History list's names (the oldest first) and where it is now.</summary>
    internal (List<string> Names, int Now) HistoryForTest() => ([.. undo.Reverse().Select(x => x.Label).Append(Current().Label).Concat(redo.Select(x => x.Label))], undo.Count);
    internal void HistoryJumpForTest(int pick) => JumpTo(pick);

    void Restore(UiState s, string what)
    {
        restoring = true;
        bool mapChanged = committed == null || committed.Map != s.Map;   // (the Bone Map table is refilled only then)
        bool paintOnly = committed != null && committed.Paint != s.Paint && (committed with { Paint = s.Paint }).Same(s);   // (updated in place: no reload)
        committed = s;
        for (int i = 0; i < parts.Rows.Count && i < s.Parts.Length; i++)
            if ((parts.Rows[i].Cells["use"].Value is true) != s.Parts[i]) parts.Rows[i].Cells["use"].Value = s.Parts[i];
        if (smooth.Checked != s.Subdivide) smooth.Checked = s.Subdivide;
        if (material.SelectedIndex != s.Material) material.SelectedIndex = s.Material;
        if (RigsText() != s.Rigs) SetRigsText(s.Rigs);
        if (Math.Abs(sizeSlider.Value - s.Size) > 1e-4) { sizeSlider.Value = s.Size; preview.Size = s.Size; }
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
        if (PaintText() != s.Paint) RestorePaint(s.Paint);
        undoButton.Enabled = undo.Count > 0 || propUndo.Count > 0; redoButton.Enabled = redo.Count > 0 || propRedo.Count > 0;
        Log(what + ": back to the earlier parts / Smooth / material / size / bone map / FBX edits / weight paint.");
        // a paint-only step: the model's weights rebuilt in place (no "Loading…", Kurt 2026-10-25); anything else rebuilds
        if (paintOnly && preview.ApplyPaintInPlace(s.Paint)) { restoring = false; UpdateStatus(); return; }
        if (mapChanged) FillMap();
        SchedulePreview();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool typing = ActiveControl is TextBox || (ActiveControl is ContainerControl c && c.ActiveControl is TextBox)
            || ActiveControl is DataGridView { IsCurrentCellInEditMode: true };   // (a grid cell being typed in: B / W are letters there)
        if (Shortcuts.Is("view.full", keyData)) { preview.ToggleFull(); return true; }
        if (!typing && Shortcuts.Is("view.pause", keyData) && preview.CancelBrushAdjust()) return true;   // (F / Shift+F: the old brush back)
        if (!typing && Shortcuts.Is("view.pause", keyData) && preview.PausePlayback()) return true;
        if (!typing && Shortcuts.Is("paint.radius", keyData) && preview.BeginBrushAdjust(false)) return true;   // (painting: Blender's F radius / Shift+F strength)
        if (!typing && Shortcuts.Is("paint.strength", keyData) && preview.BeginBrushAdjust(true)) return true;
        if (!typing && Shortcuts.Is("view.play", keyData) && preview.TogglePlayback()) return true;   // (P: play / pause in every 3D view)
        if (!typing && Shortcuts.Is("view.quad", keyData) && preview.ToggleQuad()) return true;   // (quad view)
        if (!typing && Shortcuts.Is("model.bones", keyData)) { preview.ShowBones = !preview.ShowBones; return true; }   // (Kurt, 2026-10-23)
        if (!typing && Shortcuts.Is("model.weights", keyData)) { preview.CycleWeights(); return true; }   // (off → 50 % over the model → weights alone)
        if (!typing && Shortcuts.Is("model.boneup", keyData) && StepBone(true)) return true;   // ([ / ]: up and down the bones)
        if (!typing && Shortcuts.Is("model.bonedown", keyData) && StepBone(false)) return true;
        if (!typing && Shortcuts.Is("model.paint", keyData)) { preview.Painting = !preview.Painting; return true; }   // (Paint on / off)
        if (!typing && Shortcuts.Is("model.undo", keyData)) { Undo(); return true; }
        if (!typing && Shortcuts.Is("model.redo", keyData)) { Redo(); return true; }
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
