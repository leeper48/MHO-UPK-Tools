using MhoPackageModifier;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The editor's Powers tab (Kurt, 2026-10-01: a power customizer; how the MHO Hero Creator does it, without anything the
/// server has to know): the hero's powers with their icons, a color per power (hue, saturation, brightness) previewed in
/// 3D, and on Save each colored power's stock packages recolored (PowerRecolor) into the mod's packages. A color changes
/// that power for every costume of the hero: the files are the game's own power packages.
/// </summary>
sealed partial class ModEditorView
{
    StorePreview? powerPreview;
    readonly ListBox powerList = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false, BorderStyle = BorderStyle.None };
    LightSlider? hueSlider, satSlider, brightSlider;
    readonly Label powerCaption = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 4) };
    readonly Label powerShared = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 2, 0, 4), MaximumSize = new Size(560, 0) };
    readonly ColorSwatch[] presetSwatches = new ColorSwatch[5];
    int sharedRequest;
    bool fillingPower;
    Fx.GameData? powerDb;

    /// <summary>Esc stops the Powers tab's animation (as on the Mods tab), else does what it did.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && powerPreview != null && powerPreview.Visible && powerPreview.PausePlayback()) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    Control PowersPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        if (editing == null || game == null)
        {
            var note = new Label { Text = "Save the mod once, then come back here: power colors are previewed on its saved model.", AutoSize = true, Tag = "subtle", Padding = new Padding(8) };
            return Page(note, Toolbar(), "Recolor the hero's powers (every costume of that hero shows them).");
        }
        // The 3D preview is made when the tab first shows, after the editor is themed: the theme gives every control its
        // parent's (transparent) background, which the 3D view's surface can't take (it crashed the editor).
        var host = new Panel { Dock = DockStyle.Fill };
        host.VisibleChanged += (_, _) =>
        {
            if (!host.Visible || powerPreview != null || editing == null || game == null) return;
            powerPreview = new StorePreview { Dock = DockStyle.Fill, CookedFolder = game.Cooked, Catalog = catalog, ForceEffects = true, PowerStrip = false, Always3D = true };
            powerPreview.ColorFor = proto => draft.PowerColors.FirstOrDefault(e => e.Power.Equals(proto, StringComparison.OrdinalIgnoreCase)) is { } e ? e.Color : null;
            // Packages built for colors before are left out of the preview: the color shown is the one being set, on the stock files.
            var built = editing.Manifest.PowerColors?.SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            powerPreview.SkipModFile = f => built.Contains(f);
            powerPreview.HeroPowersLoaded += FillPowers;
            // Its buttons (made when a model loads) take the app's button look; the theme pass that gives it them in the
            // main window has run already here.
            powerPreview.ControlAdded += (_, _) => Ui.RestyleButtons(powerPreview);
            host.Controls.Add(powerPreview);
            powerPreview.Mod = editing;
        };
        page.Controls.Add(host, 0, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(8, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int k = 0; k < 7; k++) right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(new Label { Text = "POWERS", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 4) }, 0, 0);
        powerList.ItemHeight = (int)(40 * S);
        powerList.BackColor = Ui.Card; powerList.ForeColor = Ui.Text;
        powerList.DrawItem += DrawPowerItem;
        powerList.SelectedIndexChanged += (_, _) => PowerSelected();
        right.Controls.Add(powerList, 0, 1);
        hueSlider = new LightSlider { Label = "Hue", Min = -180, Max = 180, Step = 1, Mark = 0, Format = v => $"{v:+0;-0;0}°", Home = () => 0, Dock = DockStyle.Top, Height = (int)(30 * S) };
        satSlider = new LightSlider { Label = "Saturation", Min = 0, Max = 2, Step = 0.01f, Mark = 1, Format = v => $"{v * 100:0} %", Home = () => 1, Dock = DockStyle.Top, Height = (int)(30 * S) };
        brightSlider = new LightSlider { Label = "Brightness", Min = 0, Max = 3, Step = 0.01f, Mark = 1, Format = v => $"{v * 100:0} %", Home = () => 1, Dock = DockStyle.Top, Height = (int)(30 * S) };
        foreach (var sl in new[] { hueSlider, satSlider, brightSlider }) { sl.ValueChanged += SliderChanged; right.Controls.Add(sl); }
        Ui.Tip(hueSlider, "Turns the power's colors around the color wheel (the brightness of each color is kept). Double-click: back to the game's.");
        Ui.Tip(satSlider, "How strong the power's colors are: 0 % is grey, 100 % as the game has it. Double-click: back to 100 %.");
        Ui.Tip(brightSlider, "How bright the power's colors are. Double-click: back to 100 %.");
        var reset = Ui.FlatButton("Game's Colors", () => SetColor(null), tip: "Take this power back to the game's own colors.");
        var all = Ui.FlatButton("Apply to All Powers", ApplyToAll, tip: "Give every power of the hero the color on the sliders (the game's colors take them all back).");
        right.Controls.Add(Toolbar(reset, all, powerCaption), 0, 5);
        // Five presets (as the MHO Hero Creator's): click = use on this power, right-click = store the sliders there.
        var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Padding = new Padding(0, 2, 0, 2) };
        presets.Controls.Add(new Label { Text = "Presets", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 7, 8, 0) });
        for (int i = 0; i < 5; i++)
        {
            int k = i;
            var sw = new ColorSwatch { Size = new Size((int)(30 * S), (int)(26 * S)), Margin = new Padding(0, 2, (int)(5 * S), 0), Number = k + 1 };
            sw.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) StorePreset(k); else if (e.Button == MouseButtons.Left) UsePreset(k); };
            presetSwatches[k] = sw;
            presets.Controls.Add(sw);
        }
        ShowPresets();
        right.Controls.Add(presets, 0, 6);
        right.Controls.Add(PaletteSection(), 0, 7);
        right.Controls.Add(powerShared, 0, 8);
        page.Controls.Add(right, 1, 0);
        Disposed += (_, _) => powerPreview?.Dispose();
        return Page(page, Toolbar(), "A color recolors the power's own game files (its particles, effect textures and material colors): every costume of the hero shows it, for anyone using this mod. Save writes them into the mod's packages.");
    }

    void FillPowers()
    {
        if (powerPreview == null) return;
        string? keep = (powerList.SelectedItem as Fx.PowerList.Power)?.Prototype;
        powerList.BeginUpdate();
        powerList.Items.Clear();
        foreach (var p in powerPreview.AllHeroPowers) powerList.Items.Add(p);
        powerList.EndUpdate();
        int at = keep == null ? -1 : powerPreview.AllHeroPowers.ToList().FindIndex(p => p.Prototype == keep);
        if (powerList.Items.Count > 0) powerList.SelectedIndex = Math.Max(0, at);
        if (powerList.Items.Count == 0) powerCaption.Text = "No Powers Found for This Hero";
    }

    /// <summary>Who else the selected power's recolor changes (its packages' other owners: a team-up's copy, Rogue …),
    /// worked out in the background.</summary>
    void ShowShared(Fx.PowerList.Power p)
    {
        if (game == null) return;
        int req = ++sharedRequest;
        string? hero = draft.Packages.Select(x => HeroOf.Package(x.File, game?.Cooked)).FirstOrDefault(h => h != null);
        if (hero == null) { powerShared.Text = ""; return; }
        powerShared.Text = "";
        var g = game;
        Task.Run(() =>
        {
            powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
            int n = PowerRecolor.PackagesOf(powerDb, p.Prototype, hero, g.Cooked).Count;
            return (n, PowerRecolor.SharedWith(powerDb, p.Prototype, hero, g.Cooked));
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != sharedRequest || t.Status != TaskStatus.RanToCompletion) return;
            var (n, shared) = t.Result;
            powerShared.Text = $"Its color goes into {n} package(s) (its own and what it sets off: missiles, hotspots, conditions, summons)."
                + (shared.Count > 0 ? "\nAlso changes, as they use the same effects: " + string.Join(", ", shared) + "." : "");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The sliders' color on every power of the hero (Kurt: one color shift for the whole hero).</summary>
    void ApplyToAll()
    {
        if (powerPreview == null || hueSlider == null || satSlider == null || brightSlider == null) return;
        var c = new PowerColor(hueSlider.Value, satSlider.Value, brightSlider.Value);
        foreach (var p in powerPreview.AllHeroPowers)
        {
            var e = EntryOf(p.Prototype);
            if (e == null) { if (c.IsNone) continue; draft.PowerColors.Add(e = new PowerColorEntry { Power = p.Prototype, Name = p.Name }); }
            e.Hue = c.Hue; e.Saturation = c.Saturation; e.Brightness = c.Brightness;
        }
        powerCaption.Text = Ui.TitleCase(c.IsNone ? $"All {powerPreview.AllHeroPowers.Count} powers back to the game's colors" : $"Color applied to all {powerPreview.AllHeroPowers.Count} powers");
        powerList.Invalidate();
        powerPreview.RefreshPowerColor();
    }

    void StorePreset(int k)
    {
        if (hueSlider == null || satSlider == null || brightSlider == null) return;
        PreviewViews.SetPowerPreset(k, new PowerColor(hueSlider.Value, satSlider.Value, brightSlider.Value));
        ShowPresets();
        powerCaption.Text = Ui.TitleCase($"Preset {k + 1} stored");
    }

    void UsePreset(int k)
    {
        if (PreviewViews.PowerPreset(k) is not { } c) { powerCaption.Text = Ui.TitleCase($"Preset {k + 1} is empty: right-click it to store the sliders"); return; }
        if (powerList.SelectedItem is not Fx.PowerList.Power || hueSlider == null || satSlider == null || brightSlider == null) return;
        fillingPower = true; hueSlider.Value = c.Hue; satSlider.Value = c.Saturation; brightSlider.Value = c.Brightness; fillingPower = false;
        SetColor(c);
    }

    void ShowPresets()
    {
        for (int k = 0; k < 5; k++)
        {
            var c = PreviewViews.PowerPreset(k);
            var sw = presetSwatches[k];
            if (sw == null) continue;
            sw.Swatch = c == null ? null : SwatchColor(c);
            Ui.Tip(sw, c == null ? $"Preset {k + 1} (empty). Right-click: store the sliders here (kept on this PC for every mod)."
                : $"Preset {k + 1}: hue {c.Hue:0}°, saturation {c.Saturation * 100:0} %, brightness {c.Brightness * 100:0} %. Click: use it on this power. Right-click: store the sliders here instead.");
        }
    }

    /// <summary>A lightning blue turned by the color, as a hint of what it does (the swatches and the list's dots).</summary>
    static Color SwatchColor(PowerColor c)
    {
        var v = c.Apply(new System.Numerics.Vector3(0.25f, 0.35f, 1f));
        float m = Math.Max(1f, Math.Max(v.X, Math.Max(v.Y, v.Z)));
        return Color.FromArgb(Math.Clamp((int)(v.X / m * 255), 0, 255), Math.Clamp((int)(v.Y / m * 255), 0, 255), Math.Clamp((int)(v.Z / m * 255), 0, 255));
    }

    PowerColorEntry? EntryOf(string proto) => draft.PowerColors.FirstOrDefault(e => e.Power.Equals(proto, StringComparison.OrdinalIgnoreCase));

    void PowerSelected()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p || hueSlider == null || satSlider == null || brightSlider == null) return;
        fillingPower = true;
        var e = EntryOf(p.Prototype);
        hueSlider.Value = e?.Hue ?? 0; satSlider.Value = e?.Saturation ?? 1; brightSlider.Value = e?.Brightness ?? 1;
        fillingPower = false;
        powerCaption.Text = (e == null ? "The Game's Colors" : "Recolored")
            // (a proc, passive or talent: its effects play in the game during other powers, there's nothing to play here)
            + (p.Animations.Count == 0 ? " · No Animation of Its Own: Its Effects Show During Other Powers" : "");
        if (p.Animations.Count > 0) powerPreview?.PlayPower(p.Prototype);
        ShowShared(p);
        LoadPalette(p);
        ShowMapRows();
    }

    void SliderChanged()
    {
        if (fillingPower || hueSlider == null || satSlider == null || brightSlider == null) return;
        SetColor(new PowerColor(hueSlider.Value, satSlider.Value, brightSlider.Value));
    }

    /// <summary>The selected power's color (null or no change: the game's); the preview shows it at once.</summary>
    void SetColor(PowerColor? c)
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p) return;
        var e = EntryOf(p.Prototype);
        if (c == null || c.IsNone)
        {
            if (e != null) { e.Hue = 0; e.Saturation = 1; e.Brightness = 1; if (c == null) e.Maps = null; }   // kept until Save, so its old packages are dropped then
            if (c == null && hueSlider != null && satSlider != null && brightSlider != null) { fillingPower = true; hueSlider.Value = 0; satSlider.Value = 1; brightSlider.Value = 1; fillingPower = false; }
        }
        else
        {
            if (e == null) draft.PowerColors.Add(e = new PowerColorEntry { Power = p.Prototype, Name = p.Name });
            e.Hue = c.Hue; e.Saturation = c.Saturation; e.Brightness = c.Brightness;
        }
        powerCaption.Text = EntryOf(p.Prototype) is { } now && !now.Color.IsNone ? "Recolored" : "The Game's Colors";
        powerList.Invalidate();
        powerPreview?.RefreshPowerColor();
        if (c == null) ShowMapRows();
    }

    void DrawPowerItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || powerList.Items[e.Index] is not Fx.PowerList.Power p) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? Ui.CardHover : Ui.Card)) e.Graphics.FillRectangle(b, e.Bounds);
        int pad = (int)(4 * S), icon = e.Bounds.Height - 2 * pad;
        var ir = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + pad, icon, icon);
        if (p.Icon != null && game != null && Fx.PowerList.Icon(p.Icon, game.Cooked) is { } img) e.Graphics.DrawImage(img, ir);
        var entry = EntryOf(p.Prototype);
        // A swatch of the color change (the hue turned on a neutral blue), when there is one.
        if (entry != null && !entry.Color.IsNone)
        {
            // (a power with replaced colors shows its first replacement's new color)
            using var sw = new SolidBrush(entry.Maps is { Count: > 0 } ms && ColorMap.FromHex(ms[0].To) is { } tc ? ToColor(tc) : SwatchColor(entry.Color));
            int s = (int)(14 * S);
            e.Graphics.FillEllipse(sw, e.Bounds.Right - s - 2 * pad, e.Bounds.Y + (e.Bounds.Height - s) / 2, s, s);
        }
        var tr = Rectangle.FromLTRB(ir.Right + 2 * pad, e.Bounds.Y, e.Bounds.Right - (int)(28 * S), e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, p.Name, Ui.Regular(10f), tr, Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Before saving: the power colors into the draft's packages (PowerColorBuild.Apply). Returns the work folder.</summary>
    string? ApplyPowerColors(List<string> log)
    {
        if (game == null) { if (draft.PowerColors.Any(e => !e.Color.IsNone)) throw new InvalidDataException("the game folder isn't set"); return null; }
        return PowerColorBuild.Apply(draft, editing, lib, game, ref powerDb, log);
    }
}
