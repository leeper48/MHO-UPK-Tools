using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Ground decals' images on the Powers tab's Parts list (Kurt, 2026-11-06: Luke Cage's "CAGE" mark; "opening in the image
/// editor like GIMP with the update would be really powerful"): ✎ on a Decal row offers, per image the decal shows, Edit in the
/// image editor (each save there is taken in), Replace with an image, Export, and Back to the Game's Image. Save writes them into
/// the power's package (DecalImages.Apply), every save, so a package rebuilt by Power Colors or Parts keeps them.
/// </summary>
sealed partial class ModEditorView
{
    readonly Dictionary<string, FileSystemWatcher> decalWatch = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<(string Package, string Texture)> decalRestore = [];
    readonly HashSet<(string Package, string Part)> decalColorRestore = [];
    string? decalWork;
    int decalVersion;

    /// <summary>The 3D view shows the decal images and colors taken in but not saved yet (Kurt, 2026-11-06: an edit from GIMP
    /// didn't show): the preview's decal reader asks the draft; called with each change, which also plays the effects again.</summary>
    void ShowDecalsLive()
    {
        static bool SamePackage(string a, string b) => Path.GetFileNameWithoutExtension(a).Equals(Path.GetFileNameWithoutExtension(b), StringComparison.OrdinalIgnoreCase);
        Fx.PowerEffects.DecalImageFor = (pkg, tex) =>
            draft.DecalImages.FirstOrDefault(x => SamePackage(x.Package, pkg) && (tex.Equals(x.Texture, StringComparison.OrdinalIgnoreCase) || tex.EndsWith("." + x.Texture, StringComparison.OrdinalIgnoreCase)))
                is { } e && draft.DecalFiles.TryGetValue(e.Image, out var f) && File.Exists(f) ? f : null;
        Fx.PowerEffects.DecalColorFor = (pkg, part) =>
            draft.DecalColors.FirstOrDefault(x => SamePackage(x.Package, pkg) && x.Part.Equals(part, StringComparison.OrdinalIgnoreCase)) is { } c ? ColorMap.FromHex(c.Color) : null;
        if (!decalHooked) { decalHooked = true; Disposed += (_, _) => { Fx.PowerEffects.DecalImageFor = null; Fx.PowerEffects.DecalColorFor = null; }; }
        powerPreview?.ReloadEffects();
    }
    bool decalHooked;

    string DecalWork() => decalWork ??= Path.Combine(lib.DataFolder, "decal-work-" + Guid.NewGuid().ToString("N")[..8]);

    static string TexLeaf(string tex) => tex[(tex.LastIndexOf('.') + 1)..];

    DecalImageEntry? DecalEntry(string package, string texture) =>
        draft.DecalImages.FirstOrDefault(x => x.Package.Equals(package, StringComparison.OrdinalIgnoreCase) && x.Texture.Equals(texture, StringComparison.OrdinalIgnoreCase));

    /// <summary>The package as the mod has it now (the draft's copy), else the game's.</summary>
    string? DecalPackage(string package) =>
        draft.Packages.FirstOrDefault(p => p.File.Equals(package, StringComparison.OrdinalIgnoreCase)).Source is string s && File.Exists(s) ? s
        : game == null ? null : new Originals(lib.DataFolder, game).Find(package) ?? (StockFiles.For(game.Cooked, package) is var st && File.Exists(st) ? st : null);

    /// <summary>✎ on a Decal row: a menu per image the decal shows.</summary>
    void DecalMenu(PowerParts.Switch s)
    {
        if (game == null || DecalPackage(s.Package) is not string pkg) return;
        List<string> texs;
        try { texs = DecalImages.Textures(pkg, s.Original); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { partsNote.Text = "The decal's images can't be read: " + ex.Message; return; }
        if (texs.Count == 0) { partsNote.Text = "This decal shows no image of its own (its material is in another package)."; return; }
        string? editor = Model.ImageEditor.Find();
        var menu = new ContextMenuStrip();
        // its color (Kurt: "add the decal color too"): the material instance's color and fade curve
        var colored = draft.DecalColors.FirstOrDefault(x => x.Package.Equals(s.Package, StringComparison.OrdinalIgnoreCase) && x.Part.Equals(s.Part, StringComparison.OrdinalIgnoreCase));
        System.Numerics.Vector3? now = null;
        try { now = DecalImages.CurrentColor(pkg, DecalImages.PartMaterial(s.Package, pkg, s.Part) ?? s.Original); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        menu.Items.Add(new ToolStripMenuItem("Decal Color" + (colored != null ? $"  ·  {colored.Color}" : now is { } c0 ? $"  ·  {ColorMap.Hex(c0)}" : ""), null, (_, _) => ColorDecal(s, now))
            { Enabled = now != null, ToolTipText = now != null ? "The decal's color (its glow and fade keep their brightness)." : "This decal's material has no color of its own to change (its image's colors show: edit the image)." });
        menu.Items.Add(new ToolStripMenuItem("Game's Color", null, (_, _) => { draft.DecalColors.Remove(colored!); decalColorRestore.Add((s.Package, s.Part)); partsNote.Text = "The decal's own color comes back on Save."; ShowDecalsLive(); }) { Enabled = colored != null, ToolTipText = "Puts the game's (or the power color's) color back on Save." });
        menu.Items.Add(new ToolStripSeparator());
        foreach (string tex in texs)
        {
            ToolStripItemCollection items = menu.Items;
            var mine = DecalEntry(s.Package, tex);
            string title = TexLeaf(tex) + (mine != null ? "  ·  Changed" : "");
            if (texs.Count > 1) { var sub = new ToolStripMenuItem(title); menu.Items.Add(sub); items = sub.DropDownItems; }
            else menu.Items.Add(new ToolStripMenuItem(title) { Enabled = false });
            var edit = new ToolStripMenuItem(editor != null ? $"Edit in {Model.ImageEditor.Describe(editor)}" : "Edit in an Image Editor (None Found)", null, (_, _) => EditDecal(s, tex)) { Enabled = editor != null };
            edit.ToolTipText = "Opens the image in your image editor (Settings → Model → Choose Image Editor picks which). Each save there is taken in; Save puts it into the mod.";
            items.Add(edit);
            items.Add(new ToolStripMenuItem("Replace with an Image", null, (_, _) => ReplaceDecal(s, tex)) { ToolTipText = "Pick a PNG, JPG or BMP to show instead. Transparency is kept." });
            items.Add(new ToolStripMenuItem("Export Image", null, (_, _) => ExportDecal(s, tex)) { ToolTipText = "Save the image as it is now (the mod's, else the game's) as a PNG." });
            items.Add(new ToolStripMenuItem("Back to the Game's Image", null, (_, _) => RestoreDecal(s, tex)) { Enabled = mine != null, ToolTipText = "Puts the game's own image back on Save." });
        }
        menu.Show(Cursor.Position);
    }

    /// <summary>The image to start from: the mod's changed one, else the package's (the mod's copy, else the game's).</summary>
    string? DecalStart(PowerParts.Switch s, string tex, string png)
    {
        if (DecalEntry(s.Package, tex) is { } e && draft.DecalFiles.TryGetValue(e.Image, out var have) && File.Exists(have)) { File.Copy(have, png, overwrite: true); return null; }
        if (DecalPackage(s.Package) is not string pkg) return "the package isn't found";
        return DecalImages.ExportPng(pkg, tex, png, game?.Cooked);
    }

    /// <summary>A new image for a texture: copied to its own numbered file (so the draft changes with each one), recorded.</summary>
    void TakeDecal(PowerParts.Switch s, string tex, string file)
    {
        Directory.CreateDirectory(DecalWork());
        string copy = Path.Combine(DecalWork(), $"{Path.GetFileNameWithoutExtension(s.Package)}_{TexLeaf(tex)}_{++decalVersion}.png");
        using (var img = new Bitmap(file)) img.Save(copy, System.Drawing.Imaging.ImageFormat.Png);
        string rel = $"Decals/{Path.GetFileNameWithoutExtension(s.Package)}/{TexLeaf(tex)}.png";   // (forward slashes, as Pictures/)
        var e = DecalEntry(s.Package, tex);
        if (e == null) draft.DecalImages.Add(e = new DecalImageEntry { Package = s.Package, Texture = tex, Image = rel });
        draft.DecalFiles[e.Image] = copy;
        decalRestore.Remove((s.Package, tex));
        ShowDecalsLive();
    }

    void EditDecal(PowerParts.Switch s, string tex)
    {
        if (Model.ImageEditor.Find() is not string exe) return;
        string png = Path.Combine(DecalWork(), "edit", $"{Path.GetFileNameWithoutExtension(s.Package)}_{TexLeaf(tex)}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        if (!decalWatch.ContainsKey(png) && DecalStart(s, tex, png) is string err) { partsNote.Text = "The image can't be exported: " + err; return; }
        if (Model.ImageEditor.Open(exe, png) is string failed) { partsNote.Text = $"{Model.ImageEditor.Describe(exe)} didn't start: {failed}"; return; }
        if (!decalWatch.ContainsKey(png))
        {
            var w = new FileSystemWatcher(Path.GetDirectoryName(png)!, Path.GetFileName(png)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
            DateTime last = File.GetLastWriteTimeUtc(png);
            void Changed()
            {
                // (an editor writes in steps: wait until the file reads as a whole image)
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(250);
                    try
                    {
                        if (File.GetLastWriteTimeUtc(png) == last) return;
                        using (new Bitmap(png)) { }
                        last = File.GetLastWriteTimeUtc(png);
                        BeginInvoke(() => { TakeDecal(s, tex, png); partsNote.Text = $"{TexLeaf(tex)}: your edit is taken in ({DateTime.Now:HH:mm:ss}). Save puts it into the mod."; });
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
                }
            }
            w.Changed += (_, _) => Task.Run(Changed);
            w.Renamed += (_, _) => Task.Run(Changed);
            decalWatch[png] = w;
            Disposed += (_, _) => w.Dispose();
        }
        partsNote.Text = $"{TexLeaf(tex)} is open in {Model.ImageEditor.Describe(exe)}: each save there (as PNG, same file) is taken in. Save puts it into the mod.";
    }

    void ColorDecal(PowerParts.Switch s, System.Numerics.Vector3? now)
    {
        var had = draft.DecalColors.FirstOrDefault(x => x.Package.Equals(s.Package, StringComparison.OrdinalIgnoreCase) && x.Part.Equals(s.Part, StringComparison.OrdinalIgnoreCase));
        var start = ColorMap.FromHex(had?.Color) ?? now ?? new(1, 1, 1);
        using var dlg = new ColorDialog { FullOpen = true, Color = Color.FromArgb((int)Math.Clamp(start.X * 255, 0, 255), (int)Math.Clamp(start.Y * 255, 0, 255), (int)Math.Clamp(start.Z * 255, 0, 255)) };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string hex = ColorMap.Hex(new(dlg.Color.R / 255f, dlg.Color.G / 255f, dlg.Color.B / 255f));
        if (had == null) draft.DecalColors.Add(had = new DecalColorEntry { Package = s.Package, Part = s.Part });
        had.Color = hex;
        decalColorRestore.Remove((s.Package, s.Part));
        partsNote.Text = $"{s.Label}: {hex}. Save puts it into the mod.";
        ShowDecalsLive();
    }

    void ReplaceDecal(PowerParts.Switch s, string tex)
    {
        using var dlg = new OpenFileDialog { Title = $"An Image for {TexLeaf(tex)}", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { TakeDecal(s, tex, dlg.FileName); partsNote.Text = $"{TexLeaf(tex)} ← {Path.GetFileName(dlg.FileName)}. Save puts it into the mod."; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { Dialog.Show(this, ex.Message, "Image Not Used", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void ExportDecal(PowerParts.Switch s, string tex)
    {
        using var dlg = new SaveFileDialog { Title = $"Export {TexLeaf(tex)}", Filter = "PNG (*.png)|*.png", FileName = TexLeaf(tex) + ".png" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        partsNote.Text = DecalStart(s, tex, dlg.FileName) is string err ? "The image can't be exported: " + err : $"Saved {dlg.FileName}.";
    }

    void RestoreDecal(PowerParts.Switch s, string tex)
    {
        if (DecalEntry(s.Package, tex) is not { } e) return;
        draft.DecalImages.Remove(e);
        draft.DecalFiles.Remove(e.Image);
        decalRestore.Add((s.Package, tex));
        partsNote.Text = $"{TexLeaf(tex)}: the game's image comes back on Save.";
        ShowDecalsLive();
    }

    /// <summary>Tests: a package's first decal gets this image (as Replace with an Image does), then Save. Returns what happened.</summary>
    internal async Task<string> DecalSaveTest(string package, string png)
    {
        string trace = Path.Combine(Path.GetTempPath(), "mhoextmm_decal_test.txt");
        void T(string x) => File.AppendAllText(trace, x + "\n");
        T("package?");
        if (DecalPackage(package) is not string src) return "no package " + package;
        T("package " + src);
        var s = PowerParts.List(package, src).FirstOrDefault(k => k.Kind == "Decal" && k.Original.Length > 0);
        if (s == null) return "no decal in " + package;
        T("decal " + s.Part);
        var texs = DecalImages.Textures(src, s.Original);
        if (texs.Count == 0) return "the decal shows no image";
        T("textures " + string.Join(",", texs));
        TakeDecal(s, texs[0], png);
        if (Environment.GetEnvironmentVariable("MHO_TEST_DECALCOLOR") is { Length: > 0 } hex) draft.DecalColors.Add(new DecalColorEntry { Package = package, Part = s.Part, Color = hex });
        bool dirty = Fingerprint() != savedPrint;
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "mhoextmm_decal_test.txt"), $"taken in; dirty {dirty}; saving\n");
        string? saved = await SaveForTest();
        return $"decal {s.Label}: {texs[0]} ← {Path.GetFileName(png)}; draft changed {dirty}; saved {saved ?? "NO"}";
    }

    /// <summary>
    /// Save (after Power Colors and Parts, which rebuild packages): each package with decal images gets them written in (a
    /// package the mod didn't have comes in as the game's copy); an image taken back gets the game's own texture data back
    /// (copied from the game's package: our packages only append names, so its references hold). Returns the work folder.
    /// </summary>
    string? ApplyDecalImages(List<string> log)
    {
        if (draft.DecalImages.Count == 0 && decalRestore.Count == 0 && draft.DecalColors.Count == 0 && decalColorRestore.Count == 0) return null;
        if (game == null) throw new InvalidDataException("the game folder isn't set");
        string work = Path.Combine(lib.DataFolder, "decal-save-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var packages = draft.DecalImages.Select(x => x.Package).Concat(decalRestore.Select(x => x.Package))
            .Concat(draft.DecalColors.Select(x => x.Package)).Concat(decalColorRestore.Select(x => x.Package)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string pkg in packages)
        {
            string? stock = new Originals(lib.DataFolder, game).Find(pkg) ?? (StockFiles.For(game.Cooked, pkg) is var st && File.Exists(st) ? st : null);
            int at = draft.Packages.FindIndex(p => p.File.Equals(pkg, StringComparison.OrdinalIgnoreCase));
            string? source = at >= 0 ? draft.Packages[at].Source : stock;
            if (source == null || !File.Exists(source)) throw new InvalidDataException($"{pkg}: the game's copy isn't found");
            string current = source;
            // the game's own image back
            var back = decalRestore.Where(x => x.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase)).Select(x => x.Texture).ToList();
            // a color taken back: the material as the game has it (when the package still has it at the game's path; one Power
            // Colors copied gets its colors from the power's color again)
            foreach (var (_, part) in decalColorRestore.Where(x => x.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase)))
                if (stock != null && DecalImages.PartMaterial(pkg, stock, part) is string sm && DecalImages.PartMaterial(pkg, current, part) is string cm && sm.Equals(cm, StringComparison.OrdinalIgnoreCase)) back.Add(sm);
            if (back.Count > 0 && at >= 0 && stock != null)
            {
                byte[] restored = DecalImages.RestoreStock(current, stock, back, log);
                current = Path.Combine(work, "restored_" + pkg);
                File.WriteAllBytes(current, restored);
            }
            foreach (var c in draft.DecalColors.Where(x => x.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase)))
            {
                if (ColorMap.FromHex(c.Color) is not { } col) continue;
                string mat = DecalImages.PartMaterial(pkg, current, c.Part) ?? throw new InvalidDataException($"{pkg}: no decal {c.Part}");
                byte[] colored = DecalImages.ApplyColor(current, mat, col, log);
                current = Path.Combine(work, "colored_" + Guid.NewGuid().ToString("N")[..6] + "_" + pkg);
                File.WriteAllBytes(current, colored);
            }
            var images = draft.DecalImages.Where(x => x.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase))
                .Select(x => (x.Texture, Png: draft.DecalFiles.TryGetValue(x.Image, out var f) ? f : Path.Combine(editing?.Folder ?? "", x.Image))).ToList();
            if (images.Count > 0)
            {
                byte[] built = DecalImages.Apply(current, images, log);
                current = Path.Combine(work, pkg);
                File.WriteAllBytes(current, built);
            }
            if (current == source) continue;
            if (at >= 0) draft.Packages[at] = (pkg, current); else draft.Packages.Add((pkg, current));
        }
        decalRestore.Clear(); decalColorRestore.Clear();
        return work;
    }
}
