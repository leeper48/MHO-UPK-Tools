using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Bone Map tab's weight painting (2026-10-25, Kurt): Paint, the brushes (Draw / Add / Subtract / Blur), Weight,
/// Strength, Radius, Mirror, and Invert / Smooth for the whole bone, Clear Paint. The bone painted is the selected row's
/// (what Weights shows). Each stroke is saved to the paint file (WeightPaint, Model\weights) and is one Undo step.
/// Keys while painting: Shift = Blur, Ctrl = the other way, F = radius, Shift+F = strength (move, click to keep, Esc).
/// </summary>
sealed partial class ModelPage
{
    Button paintToggle = null!, paintInvert = null!, paintSmooth = null!, paintClear = null!;
    readonly Dictionary<PreviewPanel.Brush, Button> brushButtons = [];
    readonly LightSlider paintWeight = new() { Label = "Weight", Min = 0, Max = 1, Step = 0.05f, Mark = null, Value = 1, Home = () => 1, Format = v => $"{v:0.00}", Dock = DockStyle.Top };
    readonly LightSlider paintStrength = new() { Label = "Strength", Min = 0.01f, Max = 1, Step = 0.05f, Mark = null, Value = 0.5f, Home = () => 0.5f, Format = v => $"{v:0.00}", Dock = DockStyle.Top };
    readonly LightSlider paintRadius = new() { Label = "Radius", Min = 3, Max = 400, Step = 1, Mark = null, Value = 40, Home = () => 40, Format = v => $"{v:0} px", Dock = DockStyle.Top };
    readonly CheckBox paintMirror = new() { Text = "Mirror", AutoSize = true };
    /// <summary>The bone painted, among the selected row's (a chain's row has several).</summary>
    readonly DropDown paintBoneList = new();

    /// <summary>The paint file of the source on the target (null: no source or target).</summary>
    string? PaintPath() => ChosenPackage is not CharacterList.Item pkg ? null
        : model != null ? WeightPaint.PathFor(host.WorkFolder, model.Folder, pkg.Key)
        : sourceFbx != null ? WeightPaint.PathFor(host.WorkFolder, ImportBuild.SourceName(sourceFbx), pkg.Key)
        : null;

    /// <summary>Paint on or off. With no source (the target's own model shown), its own model from the game becomes the source
    /// first (Kurt, 2026-10-26: Paint lit but painted nothing on Thor): paint is kept as a change to a source's fit.</summary>
    async void TogglePaint()
    {
        if (!preview.Painting && (!HasSource || PropSourceOnBody) && ChosenPackage is CharacterList.Item pkg && !TargetIsProp())
        {
            Log($"Paint: {(HasSource ? "the source is a prop, not a body" : "no source is picked")}, so {pkg.Key}'s own model (From the Game) becomes the source to paint; Build into Mod writes it with your paint.");
            if (!await UseOwnModel(pkg.Key)) return;
        }
        preview.Painting = !preview.Painting;
    }

    /// <summary>The paint controls (under the Bone Map's filter row).</summary>
    Control PaintBar()
    {
        paintToggle = Ui.FlatButton("Paint", TogglePaint,
            "Weight painting, as Blender's (Shift+P): Weights turns on over the model at 50 % (W: the weights alone, then off); the left button paints the selected row's bone (Weights shows it), the middle button turns the view (Shift + middle moves it). Shift held: Blur. Ctrl held: the other way (Draw toward 1 − Weight, Add ⇄ Subtract). Ctrl+Shift+click: pick the bone under the mouse. F: radius, Shift+F: strength (move the mouse, click to keep, Esc to put it back). Each stroke is one Undo step (Ctrl+Z). The brush reaches every part it touches; seams paint as one. A prop target paints the prop in the character's hand; to paint the character, pick its body with ◀ ▶ under the preview.");
        ShortcutsUi.Attach(paintToggle, "model.paint");
        // one row (a wrapping row inside a self-sizing panel measures itself as if every button wrapped, and the bone list
        // was pushed far down, Kurt 2026-10-25)
        var bar = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        paintToggle.Margin = new Padding(0, 0, 10, 0);
        bar.Controls.Add(paintToggle);
        foreach (var (kind, tip) in new[]
        {
            (PreviewPanel.Brush.Draw, "Draw: pulls the bone's weight toward Weight (Ctrl: toward 1 − Weight)."),
            (PreviewPanel.Brush.Add, "Add: raises the bone's weight by Weight × Strength (Ctrl: lowers it)."),
            (PreviewPanel.Brush.Subtract, "Subtract: lowers the bone's weight by Weight × Strength (Ctrl: raises it)."),
            (PreviewPanel.Brush.Blur, "Blur: evens the bone's weight out with the vertices around (also Shift with any brush)."),
        })
        {
            var b = Ui.FlatButton(kind.ToString(), () => SetBrush(kind), tip);
            b.Margin = new Padding(0, 0, 4, 0);
            brushButtons[kind] = b; bar.Controls.Add(b);
        }
        paintBoneList.Margin = new Padding(8, 0, 4, 0);
        paintBoneList.Width = (int)(150 * Ui.Dpi(DeviceDpi));
        Ui.Tip(paintBoneList, "The bone the brush paints (Weights shows it alone while painting). A chain's row (hair, a cape) has several: pick one here, or Ctrl+Shift+click it in the view.");
        paintBoneList.SelectedIndexChanged += (_, _) => { if (paintBoneList.SelectedItem is string b) preview.PaintBone = b; };
        bar.Controls.Add(paintBoneList);
        paintMirror.Margin = new Padding(8, 5, 8, 0);
        Ui.Tip(paintMirror, "Paint the other side too: the same change on the mirrored bone (g_l_ ⇄ g_r_) at the mirrored place.");
        // the second row: Mirror and the whole-bone buttons (one row ran off a narrow panel)
        var bar2 = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };
        paintMirror.Margin = new Padding(0, 5, 8, 0);
        bar2.Controls.Add(paintMirror);
        paintInvert = Ui.FlatButton("Invert Bone", () => preview.WholeBone(true), "Every vertex the bone moves gets 1 − its weight (as Blender's Weights → Invert); the rest of each vertex goes to its other bones. Mirror: the other side too. One Undo step.");
        paintSmooth = Ui.FlatButton("Smooth Bone", () => preview.WholeBone(false), "Smooths the bone's weight over the whole model (0.5, 5 times: Blender's Weights → Smooth). Mirror: the other side too. One Undo step.");
        paintClear = Ui.FlatButton("Clear Paint", ClearPaint, "Takes all the painting off this model on this target (the fit's own weights come back). One Undo step.");
        foreach (var b in new[] { paintInvert, paintSmooth, paintClear }) { b.Margin = new Padding(0, 0, 4, 0); bar2.Controls.Add(b); }
        paintRow2 = bar2;
        // the three sliders side by side (one row: the bone list keeps its room, Kurt 2026-10-25)
        var sliders = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0), Dock = DockStyle.Top };
        for (int k = 0; k < 3; k++) sliders.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        int col = 0;
        foreach (var s in new[] { paintWeight, paintStrength, paintRadius })
        {
            s.Height = (int)(30 * Ui.Dpi(DeviceDpi)); s.Dock = DockStyle.Fill; s.Margin = new Padding(0, 0, col < 2 ? 8 : 0, 0);
            sliders.Controls.Add(s, col++, 0);
        }
        paintSliders = sliders;
        Ui.Tip(paintWeight, "The weight Draw paints toward, and what Add / Subtract change by (0 – 1).");
        Ui.Tip(paintStrength, "How much of the change one dab makes (0 – 1). Shift+F in the view.");
        Ui.Tip(paintRadius, "The brush circle's size on screen. F in the view.");
        paintWeight.ValueChanged += () => preview.PaintWeight = paintWeight.Value;
        paintStrength.ValueChanged += () => preview.BrushStrength = paintStrength.Value;
        paintRadius.ValueChanged += () => preview.BrushRadius = paintRadius.Value;
        paintMirror.CheckedChanged += (_, _) => preview.PaintMirror = paintMirror.Checked;
        preview.BrushChanged += () => { paintStrength.Value = preview.BrushStrength; paintRadius.Value = preview.BrushRadius; };
        preview.PaintModeChanged += () => { Ui.Lit(paintToggle, preview.Painting); UpdatePaintButtons(); };
        preview.Painted += SavePaint;
        preview.PaintNote += Log;
        preview.BrushStrength = paintStrength.Value; preview.BrushRadius = paintRadius.Value; preview.PaintWeight = paintWeight.Value;
        SetBrush(PreviewPanel.Brush.Draw);
        var box = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.RowStyles.Add(new RowStyle(SizeType.AutoSize)); box.RowStyles.Add(new RowStyle(SizeType.AutoSize)); box.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bar.Dock = DockStyle.Top; bar2.Dock = DockStyle.Top; box.Controls.Add(bar); box.Controls.Add(bar2); box.Controls.Add(sliders);
        UpdatePaintButtons();
        return box;
    }

    /// <summary>The selected row's bones into the Bone list (the first painted, or the one painted before when it's among them).</summary>
    void FillPaintBones(List<string> bones, string? prefer = null)
    {
        string? keep = prefer ?? preview.PaintBone;
        paintBoneList.Items.Clear();
        foreach (var b in bones) paintBoneList.Items.Add(b);
        int at = keep == null ? -1 : bones.FindIndex(b => b.Equals(keep, StringComparison.OrdinalIgnoreCase));
        if (bones.Count > 0) paintBoneList.SelectedIndex = Math.Max(0, at);
        preview.PaintBone = bones.Count == 0 ? null : bones[Math.Max(0, at)];
    }

    void SetBrush(PreviewPanel.Brush kind)
    {
        preview.BrushKind = kind;
        foreach (var (k, b) in brushButtons) Ui.Lit(b, k == kind);
    }

    Control? paintSliders, paintRow2;

    /// <summary>[ / ]: the bone above or below the one picked (painted) in the skeleton, its row selected; while painting, the
    /// brush paints it (Kurt, 2026-10-25).</summary>
    bool StepBone(bool up)
    {
        string? from = preview.PaintBone ?? SelectedBones().FirstOrDefault();
        if (from == null || preview.RelativeBone(from, up) is not string to) return false;
        SelectBoneRow(to);
        if (paintBoneList.Items.Cast<object>().Select(x => x as string).ToList().FindIndex(b => string.Equals(b, to, StringComparison.OrdinalIgnoreCase)) is int k && k >= 0) paintBoneList.SelectedIndex = k;
        preview.PaintBone = to;
        Log($"Bone: {to}{(up ? " (up from " : " (down from ")}{from})");
        return true;
    }

    void UpdatePaintButtons()
    {
        bool on = preview.Painting;
        // the brush's controls show while painting; off, only Paint itself (the bone list keeps its room)
        foreach (Control c in brushButtons.Values.Cast<Control>().Append(paintBoneList).Append(paintMirror).Append(paintInvert).Append(paintSmooth).Append(paintClear))
            if (c != null) c.Visible = on;
        if (paintSliders != null) paintSliders.Visible = on;
        if (paintRow2 != null) paintRow2.Visible = on;
        foreach (var b in new[] { paintInvert, paintSmooth }) if (b != null) b.Enabled = on;
        if (paintClear != null) paintClear.Enabled = PaintPath() is string p && File.Exists(p);
    }

    /// <summary>A stroke ended: its vertices' weights into the paint file, as one Undo step.</summary>
    void SavePaint(IReadOnlyCollection<int> touched)
    {
        if (PaintPath() is not string path) return;
        var file = WeightPaint.Load(path);
        var keys = preview.PaintKeys();
        foreach (int v in touched)
            if (preview.PaintedVertex(v) is var (si, local, weights) && si < keys.Count)
                file.For(keys[si]).Vertices[local] = weights;
        file.Save(path);
        PaintStep();
    }

    void ClearPaint()
    {
        if (PaintPath() is not string path || !File.Exists(path)) return;
        Protected.CheckWrite(path);
        File.Delete(path);
        preview.LastPaint = "Clear Paint";
        PaintStep();
        Log("Weight paint cleared: the fit's own weights are back.");
        if (!preview.ApplyPaintInPlace(null)) SchedulePreview();   // (in place, as a paint-only Undo)
    }

    /// <summary>The paint changed: an Undo step (the state before is the one kept), and Build knows the model changed.</summary>
    void PaintStep()
    {
        var now = Capture();
        if (committed != null && !now.Same(committed)) { undo.Push(committed); redo.Clear(); committed = now with { Label = preview.LastPaint ?? Describe(committed, now) }; }
        else committed = now;
        UpdateUndoButtons();
        UpdatePaintButtons();
        UpdateStatus();
    }

    /// <summary>The paint file's text (Undo / Redo), null for none.</summary>
    string? PaintText() => PaintPath() is string p && File.Exists(p) ? File.ReadAllText(p) : null;

    /// <summary>
    /// --model-paint-test (scratch library only): the source on the target, then Paint: a Draw dab sets the weight and is saved,
    /// Undo takes it off (file and model), Redo puts it back (applied from the file on the rebuild), Shift blurs, Invert with
    /// Mirror changes the other side too, Clear Paint removes the file.
    /// </summary>
    internal async Task<bool> TestPaint(string mff, string package, Action<string> say)
    {
        if (!await TestPick(mff, package, say)) return false;
        for (int i = 0; i < 300 && !preview.Ready; i++) await Task.Delay(100);
        await Task.Delay(1500);
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        async Task Rebuilt(int before) { for (int i = 0; i < 600 && preview.ShowCount == before; i++) await Task.Delay(100); await Task.Delay(1500); }
        if (PaintPath() is string old && File.Exists(old)) { File.Delete(old); int sc0 = preview.ShowCount; SchedulePreview(); await Rebuilt(sc0); }
        // the bones overlay and Ctrl+click picking follow the view's cameras: one view, then the quad view (Kurt, 2026-10-25)
        preview.ShowBones = true;
        Check(preview.TestPick("g_l_knee", false) == "g_l_knee", "Ctrl+click on the drawn left knee picks it (one view)");
        preview.ToggleQuad(); await Task.Delay(1500);
        Check(preview.TestPick("g_l_knee", false) == "g_l_knee", "Ctrl+click on the drawn left knee picks it (quad view, first pane)");
        preview.ToggleQuad(); await Task.Delay(800);
        // [ / ]: up and down the bones
        mapGrid.CurrentCell = null; pickedBone = "g_l_knee"; MapSelectionChanged();
        bool upOk = StepBone(true) && preview.PaintBone == "g_l_hip";
        string afterUp = preview.PaintBone ?? "-";
        bool downOk = StepBone(false) && preview.PaintBone is "g_l_knee" or "g_l_thightwist";
        Check(upOk && downOk, $"[ from g_l_knee goes to {afterUp}, ] from there to {preview.PaintBone}");
        mapGrid.CurrentCell = null; pickedBone = "g_spine02"; MapSelectionChanged();
        preview.Painting = true;
        SetBrush(PreviewPanel.Brush.Draw);
        paintWeight.Value = 1; paintStrength.Value = 1; paintRadius.Value = 30;
        preview.PaintWeight = 1; preview.BrushStrength = 1; preview.BrushRadius = 30;
        await Task.Delay(500);
        // MHO_TEST_PAINTSHOT=<png>: the Model tab as it shows with Paint on, on the Bone Map tab (the layout)
        if (Environment.GetEnvironmentVariable("MHO_TEST_PAINTSHOT") is { Length: > 0 } shot)
        {
            modelTabs.Select(1); await Task.Delay(800);
            using var bmp = new Bitmap(Width, Height); DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height)); bmp.Save(shot);
        }
        var s1 = preview.TestStroke("g_spine02", Keys.None);
        Check(s1 != null, "a point on the model under the brush");
        if (s1 is not var (center, touched)) return false;
        float painted = preview.TestWeight(center, "g_spine02");
        Check(touched.Count > 0 && painted > 0.9f, $"Draw (weight 1, strength 1) raised the bone's weight at the brush centre to {painted:0.000} (the nearest vertex is a little off centre: the falloff), {touched.Count} vertices changed");
        string? path = PaintPath();
        int saved = path != null && File.Exists(path) ? WeightPaint.Load(path).VertexCount : 0;
        Check(saved == touched.Count, $"the stroke is saved ({saved} vertices in {Path.GetFileName(path)})");
        Check(undo.Count > 0, "the stroke is an Undo step");
        int sc = preview.ShowCount;
        Undo(); await Task.Delay(800);
        float back = preview.TestWeight(center, "g_spine02");
        Check(path != null && !File.Exists(path) && back < painted - 0.01f && preview.ShowCount == sc, $"Undo: the paint file is gone and the weight is back ({back:0.000}), in place (no reload)");
        Redo(); await Task.Delay(800);
        float again = preview.TestWeight(center, "g_spine02");
        Check(path != null && File.Exists(path) && MathF.Abs(again - painted) < 0.001f && preview.ShowCount == sc, $"Redo: the paint is back ({again:0.000}), in place");
        // a full reload gives the same weights as the in-place step
        SchedulePreview(); await Rebuilt(sc);
        float reloaded = preview.TestWeight(center, "g_spine02");
        Check(MathF.Abs(reloaded - again) < 0.001f, $"a reload gives the same weight as the in-place Redo ({reloaded:0.000})");
        Check(preview.PaintBone == "g_spine02" && pickedBone == "g_spine02", $"Undo / Redo keep the bone being painted ({preview.PaintBone})");
        // Blur (Shift): the weight under the brush evens out toward its neighbours
        paintRadius.Value = 80; preview.BrushRadius = 80;
        var s2 = preview.TestStroke("g_spine02", Keys.Shift);
        Check(s2 is { Touched.Count: > 0 }, $"Shift + stroke blurs ({s2?.Touched.Count ?? 0} vertices changed)");
        // MHO_TEST_PAINTSHOT2=<png>: the whole window after the strokes (the manual's picture of painting)
        if (Environment.GetEnvironmentVariable("MHO_TEST_PAINTSHOT2") is { Length: > 0 } shot2 && FindForm() is { } win)
        {
            // (the tabs get most of the right column, not saved; the weights over the model at 50 %)
            layoutRight.RowStyles[0] = new RowStyle(SizeType.Percent, 22); layoutRight.RowStyles[2] = new RowStyle(SizeType.Percent, 78);
            preview.ShowWeights = false; preview.CycleWeights();
            modelTabs.Select(1); await Task.Delay(1500);
            using var bmp = new Bitmap(win.Width, win.Height); win.DrawToBitmap(bmp, new Rectangle(0, 0, win.Width, win.Height)); bmp.Save(shot2);
            ApplyLayout();
        }
        // Invert with Mirror: the left upper arm and the right one both change
        paintMirror.Checked = true;
        mapGrid.CurrentCell = null; pickedBone = "g_l_shoulder"; MapSelectionChanged();
        int count = WeightPaint.Load(path).VertexCount;
        preview.WholeBone(true);
        var after = WeightPaint.Load(path);
        bool rightToo = after.Sections.SelectMany(x => x.Vertices.Values).Any(w => w.ContainsKey("g_r_shoulder"));
        Check(after.VertexCount > count && rightToo, $"Invert Bone with Mirror changed both sides ({after.VertexCount - count} more vertices saved, right side: {rightToo})");
        paintMirror.Checked = false;
        // History: the steps are named, and a pick two steps back undoes two
        var (names, at) = HistoryForTest();
        Check(names.Skip(1).Count(n => n.StartsWith("Paint:")) >= 1 && names.Last().StartsWith("Invert Bone"), "History names the steps: " + string.Join(" | ", names.Skip(Math.Max(0, names.Count - 4))));
        sc = preview.ShowCount;
        HistoryJumpForTest(at - 2); await Task.Delay(800);
        var (names2, at2) = HistoryForTest();
        Check(at2 == at - 2 && names2.Count == names.Count, $"History: a pick two steps back undid two ({at} → {at2}, {names2.Count - at2 - 1} to redo)");
        HistoryJumpForTest(at); await Task.Delay(800);
        Check(HistoryForTest().Now == at, "History: a pick forward redid them");
        sc = preview.ShowCount;
        ClearPaint(); await Task.Delay(800);
        Check(path != null && !File.Exists(path), "Clear Paint removes the paint file");
        preview.Painting = false;
        return fails == 0;
    }

    void RestorePaint(string? text)
    {
        if (PaintPath() is not string p) return;
        if (text == null) { if (File.Exists(p)) { Protected.CheckWrite(p); File.Delete(p); } }
        else { Protected.CheckWrite(p); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); }
        UpdatePaintButtons();
    }
}
