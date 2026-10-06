using System.Numerics;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Powers tab's single-color replacement (Kurt, 2026-10-03: detect the colors a power's particles use and change one
/// by hex, instead of a hue shift of everything). The power's colors (PowerRecolor.Palette over its stock packages) show as
/// swatches; a click adds a row "this color → #hex" with a range (ColorMap.Tolerance), kept in the power's
/// PowerColorEntry.Maps and applied before the sliders' shift, in the preview and on Save.
/// </summary>
sealed partial class ModEditorView
{
    readonly FlowLayoutPanel paletteStrip = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Padding = new Padding(0, 2, 0, 2) };
    readonly FlowLayoutPanel mapRows = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top };
    readonly Label paletteNote = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 7, 8, 0) };
    readonly Dictionary<string, List<PowerRecolor.Swatch>> paletteCache = new(StringComparer.OrdinalIgnoreCase);
    int paletteRequest;

    /// <summary>The palette section: a caption row and the swatches, then the replacement rows.</summary>
    Control PaletteSection()
    {
        var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 0) };
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top };
        head.Controls.Add(new Label { Text = "COLORS IN THIS POWER", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 7, 10, 0) });
        head.Controls.Add(paletteNote);
        box.Controls.Add(head);
        box.Controls.Add(paletteStrip);
        box.Controls.Add(mapRows);
        return box;
    }

    static Color ToColor(Vector3 c)
    {
        float m = Math.Max(1f, Math.Max(c.X, Math.Max(c.Y, c.Z)));
        return Color.FromArgb(Math.Clamp((int)MathF.Round(c.X / m * 255), 0, 255), Math.Clamp((int)MathF.Round(c.Y / m * 255), 0, 255), Math.Clamp((int)MathF.Round(c.Z / m * 255), 0, 255));
    }

    /// <summary>The selected power's colors, read in the background from its stock packages (once per power).</summary>
    void LoadPalette(Fx.PowerList.Power p)
    {
        int req = ++paletteRequest;
        paletteStrip.Controls.Clear();
        if (paletteCache.TryGetValue(p.Prototype, out var hit)) { ShowPalette(hit); return; }
        if (game == null) return;
        if (PowerColorBuild.IsOwn(p.Prototype))
        {
            // a character's own effects: the package as the mod has it (the one kept before an earlier recolor, if any)
            string f = p.Prototype[PowerColorBuild.OwnPrefix.Length..];
            string? path = editing != null && Path.Combine(editing.Folder, ModelWork.Folder, "color_base", f) is var kb && File.Exists(kb) ? kb
                : draft.Packages.FirstOrDefault(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase)).Source;
            if (path == null) { paletteNote.Text = ""; return; }
            paletteNote.Text = "Reading…";
            var gc = game;
            Task.Run(() => PowerRecolor.Palette([path], gc.Cooked)).ContinueWith(t =>
            {
                if (IsDisposed || req != paletteRequest) return;
                if (t.Status != TaskStatus.RanToCompletion) { paletteNote.Text = "Its Colors Couldn't Be Read"; return; }
                paletteCache[p.Prototype] = t.Result;
                ShowPalette(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        string? hero = draft.Packages.Select(x => HeroOf.Package(x.File, game?.Cooked)).FirstOrDefault(h => h != null);
        string? agentCls = agentOwner.TryGetValue(p.Prototype, out var ao) ? PowerColorBuild.ClassOf(ao) : null;
        if (hero == null && agentCls == null) { paletteNote.Text = ""; return; }
        paletteNote.Text = "Reading…";
        var g = game;
        string data = lib.DataFolder;
        Task.Run(() =>
        {
            powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
            var originals = new Originals(data, g);
            var files = (agentCls != null ? PowerRecolor.PackagesOfAgent(powerDb, p.Prototype, agentCls, g.Cooked) : PowerRecolor.PackagesOf(powerDb, p.Prototype, hero!, g.Cooked))
                .Select(f => originals.Find(f) ?? StockFiles.For(g.Cooked, f)).ToList();
            return PowerRecolor.Palette(files, g.Cooked);
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != paletteRequest) return;
            if (t.Status != TaskStatus.RanToCompletion) { paletteNote.Text = "Its Colors Couldn't Be Read"; return; }
            paletteCache[p.Prototype] = t.Result;
            ShowPalette(t.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void ShowPalette(List<PowerRecolor.Swatch> pal)
    {
        paletteStrip.SuspendLayout();
        paletteStrip.Controls.Clear();
        foreach (var sw in pal)
        {
            var c = new ColorSwatch { Size = new Size((int)(30 * S), (int)(26 * S)), Margin = new Padding(0, 2, (int)(5 * S), 2), Swatch = ToColor(sw.Tint), Number = 0 };
            var tint = sw.Tint;
            c.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) AddMap(tint); };
            Ui.Tip(c, $"{ColorMap.Hex(tint)}: {sw.Share * 100:0} % of this power's colors (in its {sw.Sources}). Click: replace this color with another.");
            paletteStrip.Controls.Add(c);
        }
        paletteStrip.ResumeLayout();
        paletteNote.Text = pal.Count == 0 ? "No Colors Found" : "Click One to Replace It";
    }

    /// <summary>A replacement row for <paramref name="from"/> (or the existing one's hex box, focused).</summary>
    void AddMap(Vector3 from)
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p) return;
        string hex = ColorMap.Hex(from);
        var e = EntryOf(p.Prototype);
        if (e == null) draft.PowerColors.Add(e = new PowerColorEntry { Power = p.Prototype, Name = p.Name });
        e.Maps ??= [];
        if (!e.Maps.Any(m => m.From.Equals(hex, StringComparison.OrdinalIgnoreCase)))
            e.Maps.Add(new ColorMapEntry { From = hex, To = hex, Tolerance = 0.35f });
        ShowMapRows(focus: hex);
    }

    /// <summary>The selected power's replacement rows: from → hex (a box, with the new color's swatch), range, remove.</summary>
    void ShowMapRows(string? focus = null)
    {
        foreach (Control c in mapRows.Controls.Cast<Control>().ToList()) c.Dispose();   // (disposed while still in the window)
        if (powerList.SelectedItem is not Fx.PowerList.Power p || EntryOf(p.Prototype)?.Maps is not { Count: > 0 } maps) return;
        TextBox? focusBox = null;
        foreach (var m in maps)
        {
            var map = m;
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            var fromSw = new ColorSwatch { Size = new Size((int)(30 * S), (int)(26 * S)), Margin = new Padding(0, 3, 4, 0), Swatch = ColorMap.FromHex(map.From) is { } f ? ToColor(f) : null, Cursor = Cursors.Default };
            Ui.Tip(fromSw, $"The power's color {map.From} (and those close to it, see Range).");
            var arrow = new Label { Text = "→", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
            var toSw = new ColorSwatch { Size = new Size((int)(30 * S), (int)(26 * S)), Margin = new Padding(4, 3, 8, 0), Swatch = ColorMap.FromHex(map.To) is { } t ? ToColor(t) : null };
            Ui.Tip(toSw, "The new color. Click: a color wheel and an eyedropper (drag it onto any color on screen). A darker color also makes that part of the power dimmer.");
            var box = new TextBox { Width = (int)(90 * S), Text = map.To, Margin = new Padding(0, 3, 0, 0), CharacterCasing = CharacterCasing.Upper, MaxLength = 7 };
            Ui.Tip(box, "The new color as hex, #RRGGBB (for example #20FF40). The brightness of each part of the power is kept; a darker color dims it too.");
            box.TextChanged += (_, _) =>
            {
                if (ColorMap.FromHex(box.Text) is not { } nv) { box.ForeColor = Ui.Warn; return; }
                box.ForeColor = Ui.Text;
                map.To = ColorMap.Hex(nv);
                toSw.Swatch = ToColor(nv);
                MapsChanged();
            };
            // The swatch opens the color wheel / eyedropper; each change goes through the hex box (row, preview, all).
            toSw.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var start = ColorMap.FromHex(box.Text) is { } cur ? Color.FromArgb((int)(cur.X * 255 + 0.5f), (int)(cur.Y * 255 + 0.5f), (int)(cur.Z * 255 + 0.5f)) : Color.White;
                Color? original = ColorMap.FromHex(map.From) is { } fr ? Color.FromArgb((int)(fr.X * 255 + 0.5f), (int)(fr.Y * 255 + 0.5f), (int)(fr.Z * 255 + 0.5f)) : null;
                ColorPickerPopup.Open(this, toSw.RectangleToScreen(toSw.ClientRectangle), start, original, c => box.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}");
            };
            var range = new LightSlider { Label = "Range", Min = 0.05f, Max = 1f, Step = 0.01f, Mark = 0.35f, Format = v => $"{v:0.00}", Home = () => 0.35f, Width = (int)(220 * S), Height = (int)(30 * S), Margin = new Padding(0, 0, 4, 0) };
            range.Value = map.Tolerance;
            range.ValueChanged += () => { map.Tolerance = range.Value; MapsChanged(); };
            Ui.Tip(range, "How close a color has to be to this one to change with it: small = only this exact color, large = similar shades too. Double-click: back to 0.35.");
            var remove = Ui.FlatButton("×", () =>
            {
                if (EntryOf(p.Prototype)?.Maps is { } list) { list.Remove(map); if (list.Count == 0) EntryOf(p.Prototype)!.Maps = null; }
                ShowMapRows();
                MapsChanged();
            }, "Stop replacing this color (it goes back to the game's, or the sliders' shift).");
            row.Controls.AddRange([fromSw, arrow, box, toSw, range, remove]);
            mapRows.Controls.Add(row);
            if (focus != null && map.From.Equals(focus, StringComparison.OrdinalIgnoreCase)) focusBox = box;
        }
        Theme.ApplyTree(mapRows, Palette.Dark);
        Ui.Restyle(mapRows);
        if (focusBox != null) { focusBox.Focus(); focusBox.SelectAll(); }
        MapsChanged();
    }

    /// <summary>
    /// For --power-map-test: the Powers tab, the power whose name has <paramref name="power"/>, its colors read, the first
    /// (largest) swatch clicked and <paramref name="toHex"/> typed into its row. Lines say what happened.
    /// </summary>
    internal async Task<List<string>> PowerMapTest(string power, string toHex)
    {
        var lines = new List<string>();
        SelectTab(pages.FindIndex(x => x.Title == "Powers"));
        for (int i = 0; i < 600 && powerList.Items.Count == 0; i++) await Task.Delay(100);
        if (powerList.Items.Count == 0) { lines.Add("FAIL no powers listed"); return lines; }
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Name.Contains(power, StringComparison.OrdinalIgnoreCase));
        if (at < 0) { lines.Add("FAIL no power like " + power); return lines; }
        powerList.SelectedIndex = at;
        var p = (Fx.PowerList.Power)powerList.Items[at];
        for (int i = 0; i < 600 && !paletteCache.ContainsKey(p.Prototype); i++) await Task.Delay(100);
        if (!paletteCache.TryGetValue(p.Prototype, out var pal) || pal.Count == 0) { lines.Add("FAIL no colors read for " + p.Name); return lines; }
        lines.Add($"{p.Name}: {pal.Count} colors: {string.Join(" ", pal.Select(x => ColorMap.Hex(x.Tint) + $" {x.Share * 100:0}%"))}; {paletteStrip.Controls.Count} swatches shown");
        AddMap(pal[0].Tint);
        var box = mapRows.Controls.Cast<Control>().SelectMany(r => r.Controls.Cast<Control>()).SelectMany(c => c is TextBox ? [c] : c.Controls.Cast<Control>().Where(x => x is TextBox)).OfType<TextBox>().FirstOrDefault();
        if (box == null) { lines.Add("FAIL no hex box in the row"); return lines; }
        box.Text = toHex;
        await Task.Delay(300);
        var e = EntryOf(p.Prototype);
        bool ok = e?.Maps is { Count: 1 } m && m[0].From == ColorMap.Hex(pal[0].Tint) && m[0].To.Equals(toHex, StringComparison.OrdinalIgnoreCase) && !e.Color.IsNone;
        lines.Add($"{(ok ? "PASS" : "FAIL")} row {e?.Maps?.FirstOrDefault()?.From} → {e?.Maps?.FirstOrDefault()?.To} (range {e?.Maps?.FirstOrDefault()?.Tolerance:0.00}); caption \"{powerCaption.Text}\"");
        return lines;
    }

    /// <summary>For --powers-shot: the Powers tab on power <paramref name="power"/> (name), the frame slider at
    /// <paramref name="fraction"/>; the preview control to draw.</summary>
    internal async Task<Control?> PowersShot(string power, double fraction)
    {
        SelectTab(pages.FindIndex(x => x.Title == "Powers"));
        for (int i = 0; i < 600 && powerList.Items.Count == 0; i++) await Task.Delay(100);
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Name.Equals(power, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || powerPreview == null) return null;
        powerList.SelectedIndex = at;
        for (int i = 0; i < 300 && powerPreview.FrameForTest() == null; i++) await Task.Delay(100);
        await Task.Delay(int.TryParse(Environment.GetEnvironmentVariable("MHO_SHOT_WAIT"), out int sw) ? sw : 3000);   // props, rules and their animations load in the background
        powerPreview.ScrubForTest(fraction);
        await Task.Delay(1500);
        return powerPreview;
    }

    /// <summary>For --keep-frame-test: power <paramref name="a"/>, the slider to the middle, then power <paramref name="b"/>;
    /// lines say where the slider is before and after.</summary>
    internal async Task<List<string>> KeepFrameTest(string a, string b)
    {
        var lines = new List<string>();
        SelectTab(pages.FindIndex(x => x.Title == "Powers"));
        for (int i = 0; i < 600 && powerList.Items.Count == 0; i++) await Task.Delay(100);
        int Find(string n) => powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && x.Animations.Count > 0);
        int ia = Find(a), ib = Find(b);
        if (ia < 0 || ib < 0 || powerPreview == null) { lines.Add($"FAIL powers not found ({a}: {ia}, {b}: {ib})"); return lines; }
        async Task<(string Anim, double Fraction, float Slider, float Frames)?> Wait(string? notAnim)
        {
            for (int i = 0; i < 300; i++) { if (powerPreview!.FrameForTest() is { } f && f.Anim != notAnim) return f; await Task.Delay(100); }
            return powerPreview!.FrameForTest();
        }
        powerList.SelectedIndex = ia;
        var fa = await Wait(null);
        if (fa == null) { lines.Add("FAIL the first power's animation didn't load"); return lines; }
        powerPreview.ScrubForTest(0.5);
        await Task.Delay(500);
        fa = powerPreview.FrameForTest();
        lines.Add($"{a}: {fa?.Anim} at {fa?.Fraction:0.00} (slider {fa?.Slider:0} of {fa?.Frames:0})");
        powerList.SelectedIndex = ib;
        var fb = await Wait(fa?.Anim);
        await Task.Delay(500);
        fb = powerPreview.FrameForTest();
        bool ok = fb != null && fa != null && Math.Abs(fb.Value.Fraction - fa.Value.Fraction) < 0.05;
        lines.Add($"{(ok ? "PASS" : "FAIL")} {b}: {fb?.Anim} at {fb?.Fraction:0.00} (slider {fb?.Slider:0} of {fb?.Frames:0})");
        return lines;
    }

    /// <summary>A replacement changed: the caption, the list's dot and the preview follow.</summary>
    void MapsChanged()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p) return;
        powerCaption.Text = EntryOf(p.Prototype) is { } now && !now.Color.IsNone ? "Recolored" : "The Game's Colors";
        powerList.Invalidate();
        powerPreview?.RefreshPowerColor();
    }
}
