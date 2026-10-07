using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Materials tab's Edit in Image Editor (Kurt, 2026-10-06: GIMP, Corel PHOTO-PAINT, Photoshop …, its saves applied like
/// Blender's Ctrl+S): the selected map is copied as a PNG into the mod's Model folder (materials\&lt;source&gt;\edit\) and
/// opened in the editor; a watcher takes every save there in as that map's override (a copy under a new name per save, so
/// the preview's picture cache sees it; Undo / Back to Automatic as for Replace File).
/// </summary>
sealed partial class ModelPage
{
    Button matEdit = null!;

    sealed class ImageWatch
    {
        public required FileSystemWatcher Watcher;
        public required string Material, Kind, File;
        /// <summary>A texture of the package itself (no source picked): each save replaces it in the package.</summary>
        public PackageMap? Pkg;
        public string Hash = "";
        public readonly System.Windows.Forms.Timer Delay = new() { Interval = 800 };
    }
    readonly Dictionary<string, ImageWatch> imageWatches = new(StringComparer.OrdinalIgnoreCase);

    Button EditButton()
    {
        matEdit = Ui.FlatButton("Edit in Image Editor", EditMapExternally, "Opens the selected map in your image editor (Settings ▾ → Model → Choose Image Editor: GIMP, Photoshop, Corel PHOTO-PAINT …) as a PNG copy in the mod's Model folder. Every time you save it there, the Model tab takes it in as that map (the preview updates; Back to Automatic or Undo takes it back). Keep the file name and PNG format when saving (GIMP: Ctrl+E, which exports over the PNG; Ctrl+S only saves GIMP's own .xcf).");
        Icons.Make(matEdit, "Edit in Image Editor", Icons.Pencil, MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
        return matEdit;
    }

    /// <summary>The selected map's file as the preview uses it (yours, the model's, or one made by the importer); null for none.</summary>
    string? SelectedMapFile()
    {
        if (SelectedMap() is not { } sel) return null;
        var tex = shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex;
        if (tex == null) return null;
        var e = OverridesFile() is string p && MaterialOverrides.Load(p).Materials.TryGetValue(sel.Material, out var x) ? x : null;
        return Describe(sel.Kind, tex, e).Path is string f && File.Exists(f) ? f : null;
    }

    void EditMapExternally()
    {
        if (SelectedPackageMap() is { } pm) { EditPackageMap(pm); return; }
        if (SelectedMap() is not { } sel || OverridesPath() is not string ovPath) return;
        if (SelectedMapFile() is not string source) { Log($"Image editor: {sel.Material} has no {sel.Kind.ToLowerInvariant()} map to edit (Replace File puts one in)."); return; }
        string? exe = ImageEditor.Find();
        if (exe == null)
        {
            Dialog.Show(this, "No image editor was found (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita or Paint.NET under Program Files).\n\nPick your editor's program in Settings ▾ → Model → Choose Image Editor → Another Program.", "No Image Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string dir = Path.Combine(Path.GetDirectoryName(ovPath)!, Path.GetFileNameWithoutExtension(ovPath), "edit");
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{FbxExport.SafeName(sel.Material)}_{sel.Kind.ToLowerInvariant().Replace(" ", "")}.png");
        try
        {
            if (Path.GetExtension(source).Equals(".png", StringComparison.OrdinalIgnoreCase)) File.Copy(source, file, true);
            else { using var b = new Bitmap(source); b.Save(file, System.Drawing.Imaging.ImageFormat.Png); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        { Log($"Image editor: {Path.GetFileName(file)} couldn't be written ({ex.Message}); is it still open in the editor? Save or close it there first."); return; }
        Watch(file, sel.Material, sel.Kind);
        if (ImageEditor.Open(exe, file) is string failed) { Log($"Image editor: {ImageEditor.Describe(exe)} didn't start: {failed}"); return; }
        Log($"Image editor: {sel.Material}'s {sel.Kind.ToLowerInvariant()} map is open in {ImageEditor.Describe(exe)} ({file}). Each save there comes back here; keep the name and PNG format{(ImageEditor.Describe(exe).StartsWith("GIMP") ? " (in GIMP: Ctrl+E exports over it; Ctrl+S only saves an .xcf)" : "")}.");
        status.Text = $"Editing in {ImageEditor.Describe(exe)}: Save There to Update"; status.ForeColor = Ui.Enabled;
    }

    void EditPackageMap(PackageMap m)
    {
        string? exe = ImageEditor.Find();
        if (exe == null) { Dialog.Show(this, "No image editor was found (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita or Paint.NET).\n\nPick your editor's program in Settings ▾ → Model → Choose Image Editor → Another Program.", "No Image Editor", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string file = Path.Combine(host.WorkFolder, "edit", Path.GetFileNameWithoutExtension(ChosenPackage?.Key ?? "package"), FbxExport.SafeName(m.Texture) + ".png");
        Protected.CheckWrite(Path.GetDirectoryName(file)!);
        try { if (!ExportPackageMap(m, file)) return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { Log($"Image editor: {Path.GetFileName(file)} couldn't be written ({ex.Message}); is it still open in the editor? Save or close it there first."); return; }
        Watch(file, m.Material, m.Kind, m);
        if (ImageEditor.Open(exe, file) is string failed) { Log($"Image editor: {ImageEditor.Describe(exe)} didn't start: {failed}"); return; }
        Log($"Image editor: {m.Texture} ({m.Material}'s {m.Kind.ToLowerInvariant()}, in the package) is open in {ImageEditor.Describe(exe)} ({file}). Each save there goes into the package; keep the name and PNG format{(ImageEditor.Describe(exe).StartsWith("GIMP") ? " (in GIMP: Ctrl+E exports over it; Ctrl+S only saves an .xcf)" : "")}.");
        status.Text = $"Editing in {ImageEditor.Describe(exe)}: Save There to Update"; status.ForeColor = Ui.Enabled;
    }

    void Watch(string file, string material, string kind, PackageMap? pkg = null)
    {
        if (imageWatches.Remove(file, out var old)) { old.Watcher.Dispose(); old.Delay.Dispose(); }
        var w = new ImageWatch
        {
            Watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, "*.*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size },
            Material = material, Kind = kind, File = file, Hash = HashOf(file) ?? "", Pkg = pkg,
        };
        w.Delay.Tick += (_, _) => { w.Delay.Stop(); TakeEdit(w); };
        void On(string? name) { if (name != null && name.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) Later(() => { w.Delay.Stop(); w.Delay.Start(); }); }
        // editors save in place, or to a temporary file renamed over it (Photoshop): every kind of change counts
        w.Watcher.Changed += (_, e) => On(e.Name); w.Watcher.Created += (_, e) => On(e.Name); w.Watcher.Renamed += (_, e) => On(e.Name);
        w.Watcher.EnableRaisingEvents = true;
        imageWatches[file] = w;
    }

    static string? HashOf(string file)
    {
        try { return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(file))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A save in the editor: the file (once readable and different) copied under a name of its own and put in as the map.</summary>
    void TakeEdit(ImageWatch w)
    {
        if (IsDisposed) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(w.File); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { w.Delay.Start(); return; }   // still being written: try again
        if (bytes.Length == 0) { w.Delay.Start(); return; }
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes));
        if (hash == w.Hash) return;
        try { using var ms = new MemoryStream(bytes); using var check = Image.FromStream(ms); }
        catch (ArgumentException) { Log($"Image editor: {Path.GetFileName(w.File)} isn't a picture this app reads (save it as PNG)."); return; }
        w.Hash = hash;
        if (w.Pkg != null) { ReplacePackageMap(w.Pkg, w.File); return; }
        if (OverridesPath() is not string ovPath) return;
        string dir = Path.Combine(Path.GetDirectoryName(ovPath)!, Path.GetFileNameWithoutExtension(ovPath));
        string name = $"{FbxExport.SafeName(w.Material)}_{w.Kind.ToLowerInvariant().Replace(" ", "")}_edited_{hash[..8].ToLowerInvariant()}.png";
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
        string rel = Path.Combine(Path.GetFileName(dir), name);
        string kind = w.Kind;
        ChangeOverrides((_, e) =>
        {
            switch (kind) { case "Color": e.Color = rel; break; case "Normal": e.Normal = rel; e.FlipGreen = false; break; case "Spec": e.Spec = rel; break; case "MHO Spec": e.SpecMho = rel; break; case "Spec Color": e.SpecColor = rel; break; case "Glow": e.Glow = rel; e.GlowOff = false; break; default: e.Alpha = rel; break; }
        }, w.Material, $"{w.Material}'s {kind.ToLowerInvariant()} map from your image editor (saved {DateTime.Now:HH:mm:ss}) is in now.");
        status.Text = $"Image Editor: {w.Material} {kind} Updated"; status.ForeColor = Ui.Enabled;
    }

    /// <summary>Test (--model-tab-test, MHO_TEST_EDITOR=1): the first material's color map opened "in the editor" (nothing
    /// starts), a corner painted and saved into the edit copy as an editor would, then the map must be that save.</summary>
    internal async Task<bool> TestImageEdit(Action<string> say)
    {
        int row = Enumerable.Range(0, matGrid.Rows.Count).FirstOrDefault(i => matGrid.Rows[i].Tag is ValueTuple<string, string> t && t.Item2 == "Color" && File.Exists(matGrid.Rows[i].Cells["from"].ToolTipText ?? ""), -1);
        if (row < 0) { say("FAIL image editor: no color map row with a file"); return false; }
        matGrid.CurrentCell = matGrid.Rows[row].Cells["material"];
        var (material, _) = ((string, string))matGrid.Rows[row].Tag!;
        EditMapExternally();
        var w = imageWatches.Values.FirstOrDefault(x => x.Material == material && x.Kind == "Color");
        if (w == null) { say("FAIL image editor: no edit copy watched\n  " + log.Text.Replace("\n", "\n  ")); return false; }
        Bitmap c;
        using (var ms = new MemoryStream(File.ReadAllBytes(w.File))) using (var b = new Bitmap(ms)) c = new Bitmap(b);   // (not left open)
        using (c)
        {
            using (var g = Graphics.FromImage(c)) g.FillRectangle(Brushes.Magenta, 0, 0, Math.Max(4, c.Width / 8), Math.Max(4, c.Height / 8));
            string tmp = w.File + ".tmp.png";
            c.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            File.Move(tmp, w.File, true);   // saved by renaming over it, as Photoshop does
        }
        for (int i = 0; i < 60 && !log.Text.Contains("from your image editor"); i++) await Task.Delay(100);
        var ov = OverridesFile() is string p ? MaterialOverrides.Load(p) : null;
        bool ok = ov != null && ov.Materials.TryGetValue(material, out var e) && e.Color?.Contains("_edited_") == true;
        say($"{(ok ? "PASS" : "FAIL")} a save in the image editor came back as {material}'s color map{(ok ? "" : "\n  " + log.Text.Replace("\n", "\n  "))}");
        return ok;
    }

    /// <summary>Test: the package the package-texture test changed (another package's name, or null for the target) and its file.</summary>
    internal (string? File, string? Path)? TestChanged { get; private set; }

    internal async Task<bool> TestPackageMaps(Action<string> say)
    {
        for (int i = 0; i < 600 && packageMaps == null; i++) await Task.Delay(100);
        say($"package maps: {(packageMaps == null ? "none" : string.Join(", ", packageMaps.Select(m => $"{m.Material}/{m.Kind}={m.Texture} {m.W}x{m.H} {m.Format}")))}");
        // Ctrl+click on the model picks its material (the Materials tab showing)
        modelTabs.Select(Enumerable.Range(0, modelTabs.Count).First(i => modelTabs.TitleAt(i) == "Materials"));
        await Task.Delay(1500);
        string? atCenter = preview.TestMaterialAtCenter();
        if (atCenter != null) SelectMaterialRow(atCenter);
        bool picked = atCenter != null && SelectedPackageMap()?.Material == atCenter && preview.PickMaterials;
        if (atCenter == null) say("SKIP Ctrl+click at the view's middle: nothing of the model there (Jean Grey's Phoenix wings leave a gap)");
        else
        {
            say($"{(picked ? "PASS" : "FAIL")} Ctrl+click at the view's middle picks a material: {atCenter} (row: {SelectedPackageMap()?.Material ?? "none"})");
            if (!picked) return false;
        }
        // MHO_TEST_MAPFROM=other: a texture the model takes from another package (an imported material)
        bool other = Environment.GetEnvironmentVariable("MHO_TEST_MAPFROM") == "other";
        int row = Enumerable.Range(0, matGrid.Rows.Count).FirstOrDefault(i => matGrid.Rows[i].Tag is PackageMap pm && (other ? pm.InFile != null : pm.Kind == "Color"), -1);
        if (row < 0) { say("FAIL no Color texture listed for the package"); return false; }
        matGrid.CurrentCell = matGrid.Rows[row].Cells["material"];
        var map = (PackageMap)matGrid.Rows[row].Tag!;
        say($"{(matEdit.Enabled && matUse.Enabled ? "PASS" : "FAIL")} Edit in Image Editor and Replace File are on for a package texture");
        EditMapExternally();
        var w = imageWatches.Values.FirstOrDefault(x => x.Pkg != null);
        if (w == null) { say("FAIL no edit copy watched"); return false; }
        Bitmap c; Color paint;
        using (var ms = new MemoryStream(File.ReadAllBytes(w.File))) using (var b = new Bitmap(ms)) c = new Bitmap(b);
        using (c)
        {
            // a color the corner doesn't have yet (an earlier run may have painted it)
            paint = c.GetPixel(0, 0).G > 128 ? Color.Magenta : Color.Cyan;
            using (var g = Graphics.FromImage(c)) using (var br = new SolidBrush(paint)) g.FillRectangle(br, 0, 0, c.Width / 4, c.Height / 4);
            c.Save(w.File, System.Drawing.Imaging.ImageFormat.Png);
        }
        for (int i = 0; i < 300 && !log.Text.Contains("replaced from"); i++) await Task.Delay(100);
        string? changed = map.InFile != null
            ? host.Packages.FirstOrDefault(x => x.File.Equals(map.InFile, StringComparison.OrdinalIgnoreCase) && x.Path.Contains("builds", StringComparison.OrdinalIgnoreCase)).Path
            : ChosenPackage is CharacterList.Item p && built.TryGetValue(p.Key, out var f) ? f : null;
        bool ok = changed != null && File.Exists(changed);
        TestChanged = ok ? (map.InFile, changed) : null;
        if (ok)
        {
            var pkg = MhoPackageModifier.Package.Open(changed!);
            var mip = MhoPackageModifier.TextureExport.ReadBestMip(pkg, map.Export, out _, Settings.Current.CookedFolder);
            var px = mip == null ? null : MhoPackageModifier.TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _);
            ok = px != null && Math.Abs(px[0] - paint.B) < 24 && Math.Abs(px[1] - paint.G) < 24 && Math.Abs(px[2] - paint.R) < 24;   // BGRA: the painted color
            say($"  texture read back from the new package: {mip?.Width}x{mip?.Height} {mip?.Format}, first pixel BGRA {(px == null ? "-" : $"{px[0]} {px[1]} {px[2]} {px[3]}")}");
        }
        say($"{(ok ? "PASS" : "FAIL")} the edited texture is in the mod's package{(map.InFile != null ? $" ({map.InFile}, added to the mod)" : "")}" + (ok ? "" : "\n  " + log.Text.Replace("\n", "\n  ").TrimEnd()));
        return ok;
    }

    void StopImageWatches()
    {
        foreach (var w in imageWatches.Values) { w.Watcher.Dispose(); w.Delay.Stop(); w.Delay.Dispose(); }
        imageWatches.Clear();
    }
}
