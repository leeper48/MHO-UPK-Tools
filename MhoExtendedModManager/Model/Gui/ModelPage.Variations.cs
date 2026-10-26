using System.Text.Json;
using MhoExtendedModManager.Gui;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Variations tab (Kurt, 2026-10-26: his ball squirrels came out brown, gray or red): every class that uses the target's
/// model (its own class, and the summon classes that name it: Pet Squirrel Modern and Triple Squirrel use the pet squirrel's)
/// with the materials the game picks from at random each time it spawns (<see cref="MaterialVariations"/>). A tile per
/// material, ticked when it's in the class's list: untick to take it out (none ticked: always the model's own material). The
/// first tile is the model's own first material ("Your Model"), so a built model can be one of the choices. Add from Image /
/// Add Hue Shift make a new material in the model's package (a copy of its first material with a new color map) and tick it.
///
/// What's chosen is kept in the work folder (variations\variations.json): each class's list (the model's own material as
/// <see cref="OwnMaterial"/>, resolved when written) and the added materials (image or hue), which are put back after every
/// Build into Mod of the model's package (a build starts from the package before the Model tab's builds): a hue variation is
/// made again from the newly built model's color map, so it follows the model.
/// </summary>
sealed partial class ModelPage
{
    const string OwnMaterial = "<model>";

    sealed class VarAdded
    {
        public string Package { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        /// <summary>"hue" (degrees from the model's color map) or "image" (Image, in the work folder).</summary>
        public string Kind { get; set; } = "hue";
        public float Hue { get; set; }
        public string? Image { get; set; }
    }

    sealed class VarState
    {
        /// <summary>Class package file → the list asked for (material paths; <see cref="OwnMaterial"/> for the model's own).</summary>
        public Dictionary<string, List<string>> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Class package file → every material shown for it (the game's list and the added ones), so an unticked one
        /// can be ticked again.</summary>
        public Dictionary<string, List<string>> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<VarAdded> Added { get; set; } = [];
        /// <summary>Class packages this tab added to the mod (they leave it when their list is the game's again).</summary>
        public List<string> AddedPackages { get; set; } = [];
        /// <summary>The game's own variations whose color maps were edited (Edit in Image Editor): put back after a build of
        /// their package.</summary>
        public List<VarEdit> Edits { get; set; } = [];
    }

    sealed class VarEdit
    {
        public string Package { get; set; } = "";
        public string Material { get; set; } = "";
        /// <summary>The edited PNG, in the work folder's variations\edit.</summary>
        public string Image { get; set; } = "";
    }

    /// <summary>Preview This: the material shown on the model's first slot in the preview (its holder package), until another
    /// target, Show the Model's Own, or a source is picked.</summary>
    (string Target, string Holder, string Material)? varPreview;

    readonly Dictionary<string, (FileSystemWatcher Watcher, System.Windows.Forms.Timer Delay)> varWatches = new(StringComparer.OrdinalIgnoreCase);

    readonly FlowLayoutPanel varBody = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
    VarState varState = new();
    string? varLoadedFor;
    bool varBusy;
    int varTab = -1;
    readonly Dictionary<string, Image?> varThumbs = new(StringComparer.OrdinalIgnoreCase);

    string VarFolder => Path.Combine(host.WorkFolder, "variations");
    string VarFile => Path.Combine(VarFolder, "variations.json");

    Control VariationsTab()
    {
        varBody.Resize += (_, _) => { foreach (Control c in varBody.Controls) c.Width = Math.Max(100, varBody.ClientSize.Width - 8); };
        Ui.Tip(varBody, "The materials the game picks from at random each time this character spawns (Squirrel Girl's squirrels: brown, gray, red). Untick one to take it out; none ticked: always the model's own material.");
        return varBody;
    }

    void LoadVarState()
    {
        if (varLoadedFor == host.WorkFolder) return;
        varLoadedFor = host.WorkFolder;
        try { varState = File.Exists(VarFile) ? JsonSerializer.Deserialize<VarState>(File.ReadAllText(VarFile)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { varState = new(); }
        varState.Lists = new(varState.Lists, StringComparer.OrdinalIgnoreCase);
        varState.Known = new(varState.Known, StringComparer.OrdinalIgnoreCase);
    }

    void SaveVarState()
    {
        try { Directory.CreateDirectory(VarFolder); File.WriteAllText(VarFile, JsonSerializer.Serialize(varState, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("Variations: " + ex.Message); }
    }

    /// <summary>A package as the mod has it now (its copy, else the game's stock one).</summary>
    string? VarPath(string file)
    {
        if (ModCopy(file) is string m && File.Exists(m)) return m;
        if (Settings.Current.CookedFolder is string cooked && StockFiles.For(cooked, file) is string s && File.Exists(s)) return s;
        return null;
    }

    /// <summary>The summon classes the census names for a model package (read even while summons are hidden: the Variations
    /// tab needs them for the squirrels' lists), with a label.</summary>
    static readonly Lazy<Dictionary<string, List<(string Class, string Label)>>> summonClasses = new(() =>
    {
        var map = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(ModelPage).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.model_summons.txt");
        if (s == null) return map;
        using var r = new StreamReader(s);
        var rows = new List<string[]>();
        for (string? line; (line = r.ReadLine()) != null;)
            if (line.Length > 0 && line[0] != '#' && line.Split('\t') is { Length: >= 5 } c && c[4] != "copy") rows.Add(c);
        foreach (var g in rows.GroupBy(c => (File: c[0], Cls: c[2]), x => x))
        {
            var x = new Summon(g.First()[1], g.Key.Cls, g.First()[3], g.First()[4], "", 0);
            string powers = string.Join(", ", g.Select(c => c[3]).Distinct(StringComparer.OrdinalIgnoreCase).Take(4)) + (g.Select(c => c[3]).Distinct().Count() > 4 ? " …" : "");
            if (!map.TryGetValue(g.Key.File, out var l)) map[g.Key.File] = l = [];
            l.Add((g.Key.Cls.ToLowerInvariant(), $"{SummonName(x)} ({powers})"));
        }
        return map;
    });

    /// <summary>The tab's target: the picked package of the mod, when it isn't a hero's costume (only characters a game class
    /// spawns have variations: summons, NPCs, enemies, pets).</summary>
    string? VarTarget => ChosenPackage?.Key is string f && !f.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) ? f : null;

    void ShowVariationsTab()
    {
        if (varTab < 0) return;
        modelTabs.SetTabVisible(varTab, VarTarget != null);
        if (modelTabs.SelectedIndex == varTab) FillVariations();
    }

    async void FillVariations()
    {
        if (varBusy) return;
        LoadVarState();
        string? target = VarTarget;
        varBody.SuspendLayout();
        foreach (Control c in varBody.Controls.Cast<Control>().ToList()) { varBody.Controls.Remove(c); c.Dispose(); }
        if (target == null || Settings.Current.CookedFolder is not string cooked) { varBody.ResumeLayout(); return; }
        varBody.Controls.Add(new Label { Text = "Reading the classes that use this model…", AutoSize = true, ForeColor = Ui.Subtle, Margin = new Padding(4) });
        varBody.ResumeLayout();
        var others = summonClasses.Value.GetValueOrDefault(target) ?? [];
        string? mesh = MhoSkeleton.ChosenFor(target);
        var (classes, own) = await Task.Run(() =>
        {
            var cl = MaterialVariations.ClassesFor(target, cooked, VarPath, others);
            var fm = VarPath(target) is string tp ? MaterialVariations.FirstMaterial(tp, mesh) : null;
            return (cl, fm);
        });
        if (IsDisposed || VarTarget != target) return;
        varBody.SuspendLayout();
        foreach (Control c in varBody.Controls.Cast<Control>().ToList()) { varBody.Controls.Remove(c); c.Dispose(); }
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        foreach (var c in classes) varBody.Controls.Add(ClassBlock(c, target, own, s));
        if (classes.Count == 0)
            varBody.Controls.Add(new Label { Text = "No game class spawns this model, so it has no random materials.", AutoSize = true, ForeColor = Ui.Subtle, Margin = new Padding(4) });
        foreach (Control c in varBody.Controls) c.Width = Math.Max(100, varBody.ClientSize.Width - 8);
        Theme.ApplyTree(varBody, Palette.Dark); MhoExtendedModManager.Gui.Ui.Restyle(varBody);
        varBody.ResumeLayout();
    }

    /// <summary>One class: its name, what its list does, a tile per material, and the add buttons.</summary>
    Control ClassBlock(MaterialVariations.ClassList c, string target, (int Export, string Path)? own, float s)
    {
        var block = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(2, 2, 2, 12) };
        block.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var stock = c.Stock;
        var asked = varState.Lists.GetValueOrDefault(c.File);
        var ticked = asked ?? c.Materials;
        var known = new List<string> { OwnMaterial };
        foreach (string m in stock.Concat(c.Materials).Concat(varState.Known.GetValueOrDefault(c.File) ?? []).Concat(varState.Added.Where(a => a.Package.Equals(target, StringComparison.OrdinalIgnoreCase)).Select(a => a.Path)))
            if (!known.Contains(m, StringComparer.OrdinalIgnoreCase)) known.Add(m);
        // the model's own material is also a game entry (squirrelgirl.squirrel_mat on the stock squirrel): one tile, not two
        if (own is { } o0 && known.Skip(1).Any(m => m.Equals(o0.Path, StringComparison.OrdinalIgnoreCase))) known.Remove(OwnMaterial);
        block.Controls.Add(new Label { Text = c.Label, AutoSize = true, Font = Ui.Bold(9.5f), ForeColor = Ui.Text, Margin = new Padding(0, 0, 0, 2) });
        string what = ticked.Count == 0 ? "No random materials: it always shows the model's own."
            : $"Each time it spawns, the game picks one of the {ticked.Count} ticked at random{(asked == null && c.Inherited ? $" (the list of {(Settings.Current.CookedFolder is string ck && MaterialVariations.FileOfClass(ck, c.From!) is string ff ? MaterialVariations.FileLabel(ff) : MaterialVariations.Readable(c.From!))}, which it extends)" : "")}{(asked != null ? " · changed in this mod" : "")}.";
        block.Controls.Add(new Label { Text = what, AutoSize = true, ForeColor = Ui.Subtle, Margin = new Padding(0, 0, 0, 4), MaximumSize = new Size((int)(560 * s), 0) });
        var tiles = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (string m in known)
        {
            // (the model's own material is in a list as the marker or by its name, and shown as one tile: Kurt's tile showed off
            // while the list had it on, 2026-10-26)
            bool on = ticked.Any(x => x.Equals(m, StringComparison.OrdinalIgnoreCase)
                || (m == OwnMaterial && own is { } o && x.Equals(o.Path, StringComparison.OrdinalIgnoreCase))
                || (x == OwnMaterial && own is { } o2 && m.Equals(o2.Path, StringComparison.OrdinalIgnoreCase)));
            bool isOwn = m == OwnMaterial || (own is { } o3 && m.Equals(o3.Path, StringComparison.OrdinalIgnoreCase));
            string label = isOwn ? "Your Model" : m.Split('.').Last();
            var tile = new VariationTile(s) { Checked = on, Caption = label, Image = VarThumb(m, c.File, target, own) };
            string mat = m;
            Ui.Tip(tile, isOwn ? $"The model's own first material{(own is { } oo ? $" ({oo.Path})" : "")}: tick it to make your model one of the choices." : $"{m}: click to {(on ? "take it out of" : "put it in")} the list.");
            tile.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) { _ = ToggleVariation(c, target, own, mat); return; }
                if (e.Button != MouseButtons.Right) return;
                // right-click (Kurt, 2026-10-26): remove one this tab added; the game's own can only be unticked
                var added = varState.Added.FirstOrDefault(x => x.Path.Equals(mat, StringComparison.OrdinalIgnoreCase));
                var menu = new ContextMenuStrip();
                bool previewing = varPreview is { } vp && vp.Target.Equals(target, StringComparison.OrdinalIgnoreCase) && vp.Material.Equals(mat, StringComparison.OrdinalIgnoreCase);
                menu.Items.Add(new ToolStripMenuItem(previewing ? "Show the Model's Own" : "Preview This", null, (_, _) => PreviewVariation(c, target, previewing ? null : mat))
                {
                    ToolTipText = previewing ? "The preview shows the model with its own material again." : "The preview shows the model with this material, as the game does when it picks this one at spawn (only in the preview: nothing in the mod changes).",
                });
                menu.Items.Add(new ToolStripMenuItem(on ? "Untick" : "Tick", null, (_, _) => _ = ToggleVariation(c, target, own, mat)) { ToolTipText = on ? "Take it out of this class's list." : "Put it in this class's list." });
                bool isOwnTile = mat == OwnMaterial || (own is { } ow && mat.Equals(ow.Path, StringComparison.OrdinalIgnoreCase));
                menu.Items.Add(new ToolStripMenuItem("Edit in Image Editor", null, (_, _) => EditVariationImage(c, target, mat))
                {
                    Enabled = !isOwnTile,
                    ToolTipText = isOwnTile ? "Your model's own color map: edit it in the Materials tab (right-click its color map → Edit in Image Editor)." : "Opens this material's color map in your image editor; each save there goes into the mod (the material keeps its name, so every list naming it shows the edit).",
                });
                menu.Items.Add(new ToolStripMenuItem("Remove This Variation", null, (_, _) => _ = RemoveVariation(target, added!))
                {
                    Enabled = added != null,
                    ToolTipText = added != null ? "Takes it out of every class's list and forgets it (it isn't made again after a build). Its material stays in the package unused until the next Build into Mod." : "Only the ones you added can be removed; the game's own can be unticked.",
                });
                menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
                MhoExtendedModManager.Gui.Ui.ShowAt(menu, Cursor.Position);
            };
            tiles.Controls.Add(tile);
        }
        block.Controls.Add(tiles);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };
        var fromImage = Ui.FlatButton("Add from Image", () => _ = AddVariation(c, target, own, image: true), "A new material for the random choice: a copy of the model's first material with your image (PNG, JPG or BMP) as its color map, laid out like the model's own. Ticked for this class.");
        var hue = Ui.FlatButton("Add Hue Shift", () => _ = AddVariation(c, target, own, image: false), "A new material for the random choice: the model's own color map with its hue turned (you pick how far: 120° makes brown green). Made again from your model after every Build into Mod, so it follows the model. Ticked for this class.");
        Icons.Make(fromImage, "Add from Image", Icons.Picture, s);
        Icons.Make(hue, "Add Hue Shift", Icons.Sparkle, s);
        bool canAdd = own is { Export: >= 0 };
        fromImage.Enabled = hue.Enabled = canAdd;
        if (!canAdd) Ui.Tip(hue, "The model's first material lives in another package, so a copy of it can't be made here.");
        foreach (var b in new[] { fromImage, hue }) { b.Margin = new Padding(0, 0, 6, 0); buttons.Controls.Add(b); }
        block.Controls.Add(buttons);
        return block;
    }

    Image? VarThumb(string material, string classFile, string target, (int Export, string Path)? own)
    {
        string path = material == OwnMaterial ? own?.Path ?? "" : material;
        if (path.Length == 0) return null;
        string key = $"{path}|{VarPath(target)}|{VarPath(classFile)}";
        if (varThumbs.TryGetValue(key, out var hit)) return hit;
        Image? img = null;
        // (the mod's copies first, then the game's: a save can rename a material's group in the mod's copy, Make Unique, while the
        // list still names the game's path)
        string? Stock(string f) => Settings.Current.CookedFolder is string ck && StockFiles.For(ck, f) is string sp && File.Exists(sp) ? sp : null;
        foreach (string? f in new[] { VarPath(classFile), VarPath(target), Stock(classFile), Stock(target) })
            if (f != null && MaterialVariations.ColorMap(f, path, Settings.Current.CookedFolder) is { } bmp)
            {
                img = new Bitmap(bmp, 96, 96);
                bmp.Dispose();
                break;
            }
        return varThumbs[key] = img;
    }

    /// <summary>Ticks / unticks <paramref name="material"/> in the class's list and writes it.</summary>
    async Task ToggleVariation(MaterialVariations.ClassList c, string target, (int Export, string Path)? own, string material)
    {
        var current = (varState.Lists.GetValueOrDefault(c.File) ?? c.Materials).ToList();
        bool Is(string x) => x.Equals(material, StringComparison.OrdinalIgnoreCase) || (material == OwnMaterial && own is { } o && x.Equals(o.Path, StringComparison.OrdinalIgnoreCase)) || (x == OwnMaterial && own is { } o2 && material.Equals(o2.Path, StringComparison.OrdinalIgnoreCase));
        if (current.Any(Is)) current.RemoveAll(x => Is(x));
        else current.Add(material);
        await WriteVariationList(c, target, current);
    }

    /// <summary>The class's list as asked (kept in the work folder) written into its package (added to the mod when needed).</summary>
    async Task<bool> WriteVariationList(MaterialVariations.ClassList c, string target, List<string> list)
    {
        if (varBusy || Settings.Current.CookedFolder is not string cooked) return false;
        varBusy = true;
        try
        {
            string? mesh = MhoSkeleton.ChosenFor(target);
            var known = varState.Known.GetValueOrDefault(c.File) ?? [];
            foreach (string m in c.Stock.Concat(list)) if (!known.Contains(m, StringComparer.OrdinalIgnoreCase)) known.Add(m);
            varState.Known[c.File] = known;
            bool asStock = list.SequenceEqual(c.Stock, StringComparer.OrdinalIgnoreCase);
            if (asStock) varState.Lists.Remove(c.File); else varState.Lists[c.File] = list;
            SaveVarState();
            if (asStock && varState.AddedPackages.Contains(c.File, StringComparer.OrdinalIgnoreCase) && !built.ContainsKey(c.File)
                && !varState.Edits.Any(x => x.Package.Equals(c.File, StringComparison.OrdinalIgnoreCase)))
            {
                host.RemovePackage(c.File);
                varState.AddedPackages.RemoveAll(x => x.Equals(c.File, StringComparison.OrdinalIgnoreCase));
                SaveVarState();
                Log($"{c.Label}: back to the game's list; {c.File} left the mod.");
                return true;
            }
            string? classPath = VarPath(c.File), targetPath = VarPath(target);
            if (classPath == null || targetPath == null) { Log($"{c.File}: not found"); return false; }
            var resolved = await Task.Run(() =>
            {
                var own = MaterialVariations.FirstMaterial(targetPath, mesh);
                return list.Select(x => x == OwnMaterial ? own?.Path : x).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });
            string outDir = Path.Combine(host.WorkFolder, "builds", "variations");
            Directory.CreateDirectory(outDir);
            string outFile = Path.Combine(outDir, c.File);
            byte[] bytes = await Task.Run(() => MaterialVariations.SetList(classPath, resolved));
            File.WriteAllBytes(outFile, bytes);
            if (!host.Packages.Any(p => p.File.Equals(c.File, StringComparison.OrdinalIgnoreCase)))
            {
                host.AddPackage(c.File, StockFiles.For(cooked, c.File));
                if (!varState.AddedPackages.Contains(c.File, StringComparer.OrdinalIgnoreCase)) varState.AddedPackages.Add(c.File);
                SaveVarState();
            }
            host.SetPackage(c.File, outFile);
            Log($"{c.Label}: {(resolved.Count == 0 ? "no random materials (always the model's own)" : $"picks from {resolved.Count}: {string.Join(", ", resolved.Select(r => r.Split('.').Last()))}")} · {c.File} is in the mod (Save Changes keeps it).");
            return true;
        }
        catch (Exception ex) { VarFailed("List Not Changed", $"{c.Label}'s list couldn't be written.", ex); return false; }
        finally { varBusy = false; FillVariations(); }
    }

    /// <summary>Add from Image / Add Hue Shift: a new material in the model's package, ticked for the class.</summary>
    /// <param name="presetHue">Tests: the hue turn, no question asked.</param>
    async Task<string?> AddVariation(MaterialVariations.ClassList c, string target, (int Export, string Path)? own, bool image, float? presetHue = null, string? presetImage = null)
    {
        if (varBusy || own is not { Export: >= 0 }) return null;
        var a = new VarAdded { Package = target, Kind = image ? "image" : "hue" };
        if (presetHue is float ph) a.Hue = ph;
        else if (image)
        {
            string? chosen = presetImage;
            if (chosen == null)
            {
                using var d = new OpenFileDialog { Title = "Add from Image", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                chosen = d.FileName;
            }
            try
            {
                Directory.CreateDirectory(VarFolder);
                string copy = Path.Combine(VarFolder, $"{Guid.NewGuid():N}{Path.GetExtension(chosen)}");
                File.Copy(chosen, copy);
                a.Image = Path.GetFileName(copy);
            }
            catch (Exception ex) { VarFailed("Image Not Added", $"{Path.GetFileName(chosen)} couldn't be copied into the mod's Model folder.", ex); return null; }
            Log($"Adding {Path.GetFileName(chosen)} as a variation…");
        }
        else
        {
            if (MhoExtendedModManager.Gui.Ui.Prompt(this, "Add Hue Shift", "Turn the model's colors by how many degrees? (120 makes brown green, 240 blue; negative turns the other way)", "120") is not string t
                || !float.TryParse(t.Trim().TrimEnd('°'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float deg)) return null;
            a.Hue = deg;
        }
        varBusy = true;
        string? made = null;
        try
        {
            string? targetPath = VarPath(target);
            if (targetPath == null) return null;
            var pkgNow = Package.Open(targetPath);
            string stem = MaterialOut.Safe(own.Value.Path.Split('.').Last().Replace("_mat", "")) + "_var";
            int n = 1;
            while (pkgNow.Exports.Any(e => e.ObjectName.Equals($"{stem}{n}_mat", StringComparison.OrdinalIgnoreCase))) n++;
            a.Name = $"{stem}{n}_mat";
            var (bytes, path) = await Task.Run(() => MakeVariation(targetPath, own.Value.Export, a));
            string outDir = Path.Combine(host.WorkFolder, "builds", "variations", "models");
            Directory.CreateDirectory(outDir);
            string outFile = Path.Combine(outDir, target);
            File.WriteAllBytes(outFile, bytes);
            host.SetPackage(target, outFile);
            if (built.ContainsKey(target)) built[target] = outFile;
            a.Path = path;
            varState.Added.Add(a);
            SaveVarState();
            made = path;
            Log($"{a.Name}: a {(image ? "copy of the model's first material with your image" : $"copy of the model's first material with its hue turned {a.Hue:0.#}°")}, in {target}.");
        }
        catch (Exception ex)
        {
            VarFailed("Variation Not Added", image ? $"The new material from {(a.Image != null ? "your image" : "the image")} couldn't be made." : $"The hue-shifted material ({a.Hue:0.#}°) couldn't be made.", ex,
                image ? "Try the image as a PNG of a power-of-two size (512, 1024 or 2048 pixels square), the way game textures are." : null);
        }
        finally { varBusy = false; }
        if (made == null) { FillVariations(); return null; }
        // ticked for the class: its list (the game's, or the model's own when it had none) plus the new one
        var list = (varState.Lists.GetValueOrDefault(c.File) ?? (c.Materials.Count > 0 ? c.Materials : [OwnMaterial])).ToList();
        // (a list that names the model's own first material by its path follows the model: the build renames it)
        if (own is { } o) list = list.Select(x => x.Equals(o.Path, StringComparison.OrdinalIgnoreCase) && !c.Stock.Contains(x, StringComparer.OrdinalIgnoreCase) ? OwnMaterial : x).ToList();
        list.Add(made);
        await WriteVariationList(c, target, list);
        return made;
    }

    /// <summary>
    /// Test (--model-variations-test; a scratch MHO_EXTMM_HOME, the mod isn't saved): the Variations tab through its own code on
    /// the pet squirrel's package: the tab and its classes, gray taken out of Pet Squirrel Modern, a hue-shift material added,
    /// Triple Squirrel given a list, a rebuild that loses the added material (it's made again), back to the game's list.
    /// MHO_TEST_VARSHOT=&lt;png&gt;: the tab after the edits.
    /// </summary>
    internal async Task<int> TestVariations(string target, Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        if (Settings.Current.CookedFolder is not string cooked) { say("no game folder"); return 1; }
        if (!host.Packages.Any(p => p.File.Equals(target, StringComparison.OrdinalIgnoreCase))) { host.AddPackage(target, StockFiles.For(cooked, target)); added.Add(target); }
        gameList = false; FillPackages(); Reselect(packages, target);
        await Task.Delay(300);
        Check(VarTarget == target && modelTabs.TitleAt(varTab) == "Variations", $"the Variations tab is there for {target}");
        modelTabs.Select(varTab);
        for (int i = 0; i < 100 && !varBody.Controls.OfType<TableLayoutPanel>().Any(); i++) await Task.Delay(100);
        var blocks = varBody.Controls.OfType<TableLayoutPanel>().ToList();
        var tiles = blocks.SelectMany(b => b.Controls.OfType<FlowLayoutPanel>()).SelectMany(f => f.Controls.OfType<VariationTile>()).ToList();
        say($"  {blocks.Count} classes, {tiles.Count} tiles ({string.Join(", ", tiles.Select(t => t.Caption + (t.Checked ? " (ticked)" : "")))})");
        Check(blocks.Count == 3 && tiles.Count(t => t.Checked) == 3 && tiles.All(t => t.Image != null), "three classes; Pet Squirrel Modern's brown, gray and red ticked, each tile with its picture");
        var others = summonClasses.Value.GetValueOrDefault(target) ?? [];
        MaterialVariations.ClassList ClassOf(string end) => MaterialVariations.ClassesFor(target, cooked, VarPath, others).First(c => c.Class.EndsWith(end, StringComparison.OrdinalIgnoreCase));
        (int Export, string Path)? Own() => VarPath(target) is string tp ? MaterialVariations.FirstMaterial(tp, MhoSkeleton.ChosenFor(target)) : null;
        var modern = ClassOf("modern");
        await ToggleVariation(modern, target, Own(), "squirrelgirl.squirrel_grey_mat");
        string modernFile = modern.File;
        Check(VarPath(modernFile) is string mp && host.Packages.Any(p => p.File.Equals(modernFile, StringComparison.OrdinalIgnoreCase))
            && MaterialVariations.ReadClass(mp)?.Materials?.SequenceEqual(["squirrelgirl.squirrel_mat", "squirrelgirl.squirrel_red_mat"], StringComparer.OrdinalIgnoreCase) == true,
            "gray unticked: Pet Squirrel Modern's package is in the mod with brown and red only");
        string? made = await AddVariation(ClassOf("modern"), target, Own(), image: false, presetHue: 120);
        Check(made != null && MaterialVariations.ReadClass(VarPath(modernFile)!)?.Materials?.Contains(made, StringComparer.OrdinalIgnoreCase) == true
            && MaterialVariations.ColorMap(VarPath(target)!, made, cooked) != null, $"Add Hue Shift (120 degrees): {made} is in the squirrel's package and ticked for Pet Squirrel Modern");
        // Preview This: the preview's first material shows the green one; Show the Model's Own puts its own back
        for (int i = 0; i < 100 && !preview.LooksForTest.Any(); i++) await Task.Delay(100);
        var before = preview.LooksForTest.FirstOrDefault()?.Diffuse;
        PreviewVariation(ClassOf("modern"), target, made!);
        for (int i = 0; i < 100 && (preview.LooksForTest.FirstOrDefault()?.Diffuse is not { } d1 || ReferenceEquals(d1, before)); i++) await Task.Delay(100);
        var during = preview.LooksForTest.FirstOrDefault()?.Diffuse;
        Check(during != null && !ReferenceEquals(during, before) && varPreview != null, "Preview This: the preview draws the model with the green variation");
        PreviewVariation(ClassOf("modern"), target, null);
        for (int i = 0; i < 100 && ReferenceEquals(preview.LooksForTest.FirstOrDefault()?.Diffuse, during); i++) await Task.Delay(100);
        Check(varPreview == null && !ReferenceEquals(preview.LooksForTest.FirstOrDefault()?.Diffuse, during), "Show the Model's Own: its own material again");
        // with a source picked (Kurt's case: the squirrel itself, its own model fitted to the target)
        if (!GameMode) { sourceKind.SelectedIndex = 2; await Task.Delay(300); }
        GameChosen(GameSrc + target);
        for (int i = 0; i < 300 && !(HasSource && preview.LooksForTest.Any() && !refreshing); i++) await Task.Delay(100);
        await Task.Delay(500);
        var srcBefore = preview.LooksForTest.Select(l => l?.Diffuse).ToList();
        PreviewVariation(ClassOf("modern"), target, made!);
        for (int i = 0; i < 200 && preview.LooksForTest.Select(l => l?.Diffuse).SequenceEqual(srcBefore); i++) await Task.Delay(100);
        Check(HasSource && !preview.LooksForTest.Select(l => l?.Diffuse).SequenceEqual(srcBefore), "Preview This with a source picked (the squirrel itself, fitted): the preview draws the green variation");
        PreviewVariation(ClassOf("modern"), target, null);

        // Edit in Image Editor, as a save there arrives: the added one's color map, and the game's gray squirrel's
        string edited = Path.Combine(Path.GetTempPath(), "mho_var_edit.png");
        using (var eb = new Bitmap(512, 512)) { using (var g = Graphics.FromImage(eb)) g.Clear(Color.Orange); eb.Save(edited, System.Drawing.Imaging.ImageFormat.Png); }
        Directory.CreateDirectory(Path.Combine(VarFolder, "edit"));
        string e1 = Path.Combine(VarFolder, "edit", "test_added.png"); File.Copy(edited, e1, true);
        await TakeVariationEdit(ClassOf("modern"), target, target, made!, e1);
        Color Mid(string pkgFile, string mat) { using var m = MaterialVariations.ColorMap(VarPath(pkgFile)!, mat, cooked); return m == null ? Color.Empty : m.GetPixel(m.Width / 2, m.Height / 2); }
        var c1 = Mid(target, made!);
        Check(c1.R > 200 && c1.G > 100 && c1.B < 80 && varState.Added.First(x => x.Path == made).Kind == "image", $"Edit in Image Editor on the added one: its color map is the edit ({c1.R},{c1.G},{c1.B}), and it's kept as an image");
        string e2 = Path.Combine(VarFolder, "edit", "test_grey.png"); File.Copy(edited, e2, true);
        await TakeVariationEdit(ClassOf("modern"), target, modernFile, "squirrelgirl.squirrel_grey_mat", e2);
        var c2 = Mid(modernFile, "squirrelgirl.squirrel_grey_mat");
        Check(c2.R > 200 && c2.B < 80 && varState.Edits.Any(x => x.Material == "squirrelgirl.squirrel_grey_mat"), $"and on the game's gray squirrel: Pet Squirrel Modern's copy has the edit ({c2.R},{c2.G},{c2.B})");
        varState.Edits.Clear(); SaveVarState();

        // Add from Image with an odd image: a 1000 x 600 JPG
        string jpg = Path.Combine(Path.GetTempPath(), "mho_var_test.jpg");
        using (var b = new Bitmap(1000, 600)) { using (var g = Graphics.FromImage(b)) { g.Clear(Color.Purple); g.FillRectangle(Brushes.Yellow, 100, 100, 400, 200); } b.Save(jpg, System.Drawing.Imaging.ImageFormat.Jpeg); }
        string? fromImage = await AddVariation(ClassOf("modern"), target, Own(), image: true, presetImage: jpg);
        Check(fromImage != null && MaterialVariations.ReadClass(VarPath(modernFile)!)?.Materials?.Contains(fromImage, StringComparer.OrdinalIgnoreCase) == true,
            $"Add from Image (a 1000 x 600 JPG): {fromImage ?? "nothing"} ticked for Pet Squirrel Modern");
        if (fromImage == null) say("  log: " + string.Join(" | ", log.Lines.TakeLast(6)));
        if (fromImage != null && varState.Added.FirstOrDefault(x => x.Path == fromImage) is { } fa)
        {
            await RemoveVariation(target, fa);   // (MHO_EXTMM_TEST_DIALOGS answers its question with OK)
            Check(MaterialVariations.ReadClass(VarPath(modernFile)!)?.Materials?.Contains(fromImage, StringComparer.OrdinalIgnoreCase) == false && !varState.Added.Any(x => x.Path == fromImage),
                "Remove This Variation: out of Pet Squirrel Modern's list and forgotten");
        }
        var triple = ClassOf("triplesquirrel");
        await WriteVariationList(triple, target, [OwnMaterial, made!]);
        Check(MaterialVariations.ReadClass(VarPath(triple.File)!)?.Materials?.Count == 2, "Triple Squirrel, which had no list, picks between the model's own and the green one");
        // a rebuild starts from the package before the Model tab's builds: the added material is gone, then made again
        host.SetPackage(target, StockFiles.For(cooked, target));
        Check(MaterialVariations.ColorMap(VarPath(target)!, made!, cooked) == null, "(a rebuild loses it)");
        await VariationsAfterBuild(target);
        Check(MaterialVariations.ColorMap(VarPath(target)!, made!, cooked) != null && MaterialVariations.ReadClass(VarPath(triple.File)!)?.Materials?.Count == 2, "after the build it's made again, and the lists still name it");
        FillVariations();
        for (int i = 0; i < 50 && varBusy; i++) await Task.Delay(100);
        await Task.Delay(1500);
        if (Environment.GetEnvironmentVariable("MHO_TEST_VARSHOT") is { Length: > 0 } shot)
        {
            var form = FindForm()!;
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(shot);
        }
        // back to the game's: Pet Squirrel Modern's own three, and its package leaves the mod
        await WriteVariationList(ClassOf("modern"), target, ["squirrelgirl.squirrel_mat", "squirrelgirl.squirrel_grey_mat", "squirrelgirl.squirrel_red_mat"]);
        Check(!host.Packages.Any(p => p.File.Equals(modernFile, StringComparison.OrdinalIgnoreCase)), "back to the game's list: Pet Squirrel Modern's package leaves the mod");
        return fails;
    }

    /// <summary>Preview This: the preview draws the model with <paramref name="material"/> on its first material slot (null:
    /// its own again). Only the preview changes.</summary>
    void PreviewVariation(MaterialVariations.ClassList c, string target, string? material)
    {
        if (material == null || material == OwnMaterial) { varPreview = null; RefreshPreview(); Log("Preview: the model's own material."); return; }
        string? holder = VarHolder(c.File, target, material);
        if (holder == null) holder = c.File;
        varPreview = (target, holder, material);
        RefreshPreview();   // (it applies varPreview once the model is shown)
        Log($"Preview: {material.Split('.').Last()} on the model's first material (only in the preview; the game picks one of the ticked at random).");
    }

    /// <summary>After the preview shows a model: Preview This's material on the sections of the game's first material slot.
    /// The target's own model: its sections on material slot 0. A source fitted to the target (Kurt's case, 2026-10-26: the
    /// source is the squirrel itself, linked to Blender): the parts of the material the build puts in slot 0 (the Materials
    /// tab's slot order).</summary>
    async Task ApplyVariationPreview(PreviewPanel.Prepared tp)
    {
        if (varPreview is not { } vp || !vp.Target.Equals(VarTarget ?? "", StringComparison.OrdinalIgnoreCase)) return;
        List<int>? fitted = null;
        if (HasSource)
        {
            var mats = tp.MaterialList.Select(x => x.Material).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? first = MaterialOverrides.Ordered(mats, OverridesFile()).FirstOrDefault();
            fitted = first == null ? [] : tp.SectionNames.Select((n, i) => (n, i)).Where(x => x.n.Equals(first, StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToList();
            if (fitted.Count == 0) fitted = Enumerable.Range(0, tp.Mesh.Looks.Length).ToList();
        }
        else if (tp.MainRef == null) return;
        var mr = tp.MainRef;
        string? cooked = Settings.Current.CookedFolder;
        // the game's copy too: a save can rename a material's group in the mod's copy (Make Unique)
        string?[] places = [VarPath(vp.Holder), cooked != null ? StockFiles.For(cooked, vp.Holder) : null];
        var result = await Task.Run(() =>
        {
            System.Drawing.Bitmap? bmp = null;
            foreach (string? f in places) if (f != null && File.Exists(f) && (bmp = MaterialVariations.ColorMap(f, vp.Material, cooked)) != null) break;
            if (bmp == null) return ((byte[] Px, int W, int H, List<int> Sections)?)null;
            var sections = fitted ?? ModMeshes.SectionSlots(mr!).Select((slot, i) => (slot, i)).Where(x => x.slot == 0).Select(x => x.i).ToList();
            var r = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var bd = bmp.LockBits(r, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++) System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, px, y * bmp.Width * 4, bmp.Width * 4);
            bmp.UnlockBits(bd);
            (byte[], int, int, List<int>) res = (px, bmp.Width, bmp.Height, sections);
            bmp.Dispose();
            return res;
        });
        if (result is not { } rs || IsDisposed) { Log($"Preview: {vp.Material.Split('.').Last()}'s color map wasn't found."); return; }
        preview.SetSectionDiffuse(rs.Sections, rs.Px, rs.W, rs.H);
    }

    /// <summary>The package of the mod that holds a material: the class's, else the model's (their current copies).</summary>
    string? VarHolder(string classFile, string target, string material)
    {
        foreach (string f in new[] { classFile, target })
            if (VarPath(f) is string p)
                try
                {
                    var pkg = Package.Open(p);
                    var t = new Fx.FxTables(pkg);
                    if (Enumerable.Range(0, pkg.Exports.Length).Any(k => t.PathOf(k + 1).Equals(material, StringComparison.OrdinalIgnoreCase))) return f;
                }
                catch (Exception ex) when (ex is IOException or PackageFormatException) { }
        return null;
    }

    /// <summary>Edit in Image Editor (Kurt, 2026-10-26): the material's color map as a PNG in the work folder, opened in the
    /// image editor; each save there replaces the color map in the package that holds the material (added to the mod when
    /// it isn't in it). An added variation becomes an image one (made again from the edit after a build); an edited game
    /// variation is kept in Edits and put back after a build of its package.</summary>
    void EditVariationImage(MaterialVariations.ClassList c, string target, string material)
    {
        string? exe = ImageEditor.Find();
        if (exe == null)
        {
            Dialog.Show(this, "No image editor was found (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita or Paint.NET under Program Files).\n\nTo pick your editor's program: Settings ▾ → Model → Choose Image Editor → Another Program.", "No Image Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string? holder = VarHolder(c.File, target, material);
        if (holder == null || VarPath(holder) is not string holderPath) { VarFailed("Not Opened", $"{material.Split('.').Last()} wasn't found in {c.File} or {target}.", new InvalidDataException("its material isn't in the mod's packages")); return; }
        string name = MaterialOut.Safe(material.Split('.').Last());
        string file = Path.Combine(VarFolder, "edit", name + ".png");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var map = MaterialVariations.ColorMap(holderPath, material, Settings.Current.CookedFolder) ?? throw new InvalidDataException("its color map didn't decode");
            map.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { VarFailed("Not Opened", $"{Path.GetFileName(file)} couldn't be written (is it still open in the editor? Save or close it there first).", ex); return; }
        if (varWatches.Remove(file, out var old)) { old.Watcher.Dispose(); old.Delay.Dispose(); }
        var delay = new System.Windows.Forms.Timer { Interval = 800 };
        var w = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
        void Changed() { if (IsHandleCreated) BeginInvoke(() => { delay.Stop(); delay.Start(); }); }
        w.Changed += (_, _) => Changed(); w.Created += (_, _) => Changed(); w.Renamed += (_, _) => Changed();
        delay.Tick += (_, _) => { delay.Stop(); _ = TakeVariationEdit(c, target, holder, material, file); };
        varWatches[file] = (w, delay);
        Disposed += (_, _) => { w.Dispose(); delay.Dispose(); };
        if (ImageEditor.Open(exe, file) is string failed) { VarFailed("Not Opened", $"{ImageEditor.Describe(exe)} didn't start.", new InvalidOperationException(failed)); return; }
        Log($"Image editor: {material.Split('.').Last()}'s color map is open in {ImageEditor.Describe(exe)} ({file}). Each save there goes into the mod; keep the name and PNG format{(ImageEditor.Describe(exe).StartsWith("GIMP") ? " (in GIMP: Ctrl+E exports over it; Ctrl+S only saves an .xcf)" : "")}.");
    }

    /// <summary>A save from the image editor: the color map replaced in the package that holds the material.</summary>
    async Task TakeVariationEdit(MaterialVariations.ClassList c, string target, string holder, string material, string file)
    {
        if (varBusy) { if (varWatches.TryGetValue(file, out var wd)) wd.Delay.Start(); return; }
        if (VarPath(holder) is not string holderPath || Settings.Current.CookedFolder is not string cooked) return;
        varBusy = true;
        try
        {
            string snap = Path.Combine(Path.GetTempPath(), $"mho_varedit_{Guid.NewGuid():N}.png");
            File.Copy(file, snap, true);   // (the editor may still be writing: a copy, read once)
            byte[] bytes;
            try { bytes = await Task.Run(() => MaterialVariations.ReplaceColorMap(File.ReadAllBytes(holderPath), material, snap)); }
            finally { try { File.Delete(snap); } catch (IOException) { } }
            string outDir = Path.Combine(host.WorkFolder, "builds", "variations", holder.Equals(target, StringComparison.OrdinalIgnoreCase) ? "models" : "classes");
            Directory.CreateDirectory(outDir);
            string outFile = Path.Combine(outDir, holder);
            File.WriteAllBytes(outFile, bytes);
            if (!host.Packages.Any(p => p.File.Equals(holder, StringComparison.OrdinalIgnoreCase)))
            {
                host.AddPackage(holder, StockFiles.For(cooked, holder));
                if (!varState.AddedPackages.Contains(holder, StringComparer.OrdinalIgnoreCase)) varState.AddedPackages.Add(holder);
            }
            host.SetPackage(holder, outFile);
            if (built.ContainsKey(holder)) built[holder] = outFile;
            string rel = Path.GetRelativePath(VarFolder, file);
            if (varState.Added.FirstOrDefault(x => x.Path.Equals(material, StringComparison.OrdinalIgnoreCase)) is { } a) { a.Kind = "image"; a.Image = rel; }
            else
            {
                varState.Edits.RemoveAll(x => x.Package.Equals(holder, StringComparison.OrdinalIgnoreCase) && x.Material.Equals(material, StringComparison.OrdinalIgnoreCase));
                varState.Edits.Add(new VarEdit { Package = holder, Material = material, Image = rel });
            }
            SaveVarState();
            Log($"Image editor: {material.Split('.').Last()}'s color map is in {holder} now (Save Changes keeps it).");
        }
        catch (Exception ex) { VarFailed("Edit Not Taken In", $"The saved image couldn't go into {material.Split('.').Last()}.", ex, "Keep the image a PNG; any size works."); }
        finally { varBusy = false; }
        varThumbs.Clear();
        FillVariations();
    }

    /// <summary>Remove This Variation: out of every class's list (each written again) and forgotten.</summary>
    async Task RemoveVariation(string target, VarAdded a)
    {
        if (varBusy || Settings.Current.CookedFolder is not string cooked) return;
        if (Dialog.Show(this, $"Remove {a.Name}? It comes out of every class's list here and isn't made again after a build. Its material stays in the package unused until the next Build into Mod.",
                "Remove a Variation", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        varState.Added.Remove(a);
        foreach (var k in varState.Known.Keys.ToList()) varState.Known[k].RemoveAll(x => x.Equals(a.Path, StringComparison.OrdinalIgnoreCase));
        var touched = varState.Lists.Where(kv => kv.Value.Contains(a.Path, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
        SaveVarState();
        var others = summonClasses.Value.GetValueOrDefault(target) ?? [];
        var classes = await Task.Run(() => MaterialVariations.ClassesFor(target, cooked, VarPath, others));
        foreach (var c in classes.Where(c => touched.Contains(c.File, StringComparer.OrdinalIgnoreCase)))
            await WriteVariationList(c, target, varState.Lists[c.File].Where(x => !x.Equals(a.Path, StringComparison.OrdinalIgnoreCase)).ToList());
        Log($"{a.Name} removed.");
        FillVariations();
    }

    /// <summary>The added material made in the model's package: its image, or the first material's color map turned.</summary>
    (byte[] Bytes, string Path) MakeVariation(string targetPath, int firstMaterialExport, VarAdded a)
    {
        string image;
        if (a.Kind == "image" && a.Image != null) image = Path.Combine(VarFolder, a.Image);
        else
        {
            var first = MaterialVariations.FirstMaterial(targetPath, MhoSkeleton.ChosenFor(a.Package)) ?? throw new InvalidDataException("the model's first material wasn't found");
            using var map = MaterialVariations.ColorMap(targetPath, first.Path, Settings.Current.CookedFolder) ?? throw new InvalidDataException("the model's color map didn't decode");
            Directory.CreateDirectory(VarFolder);
            image = Path.Combine(VarFolder, $"{a.Name}.png");
            MaterialVariations.HueShift(map, a.Hue, image);
        }
        return MaterialVariations.AddMaterial(File.ReadAllBytes(targetPath), firstMaterialExport, image, a.Name);
    }

    /// <summary>
    /// After a Build into Mod of <paramref name="file"/>: the added materials it lost (a build starts from the package before
    /// the Model tab's builds) made again from the new model (a hue one from its color map), and the lists of the classes that
    /// name the model's own material written again (its first material has a new name after a build).
    /// </summary>
    async Task VariationsAfterBuild(string file)
    {
        LoadVarState();
        var mine = varState.Added.Where(a => a.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        // (not only when something was changed here: the note about random materials is for a first build too)
        if (file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) return;
        string? path = VarPath(file);
        if (path == null) return;
        try
        {
            string? mesh = MhoSkeleton.ChosenFor(file);
            var (bytes, notes) = await Task.Run(() =>
            {
                byte[] cur = File.ReadAllBytes(path);
                var notes = new List<string>();
                foreach (var a in mine)
                {
                    var pkg = Package.FromBytes(cur);
                    if (pkg.Exports.Any(e => e.ObjectName.Equals(a.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    string tmp = Path.Combine(Path.GetTempPath(), $"mho_var_{Guid.NewGuid():N}.upk");
                    try
                    {
                        File.WriteAllBytes(tmp, cur);
                        var first = MaterialVariations.FirstMaterial(tmp, mesh);
                        if (first is not { Export: >= 0 } fm) { notes.Add($"{a.Name}: the model's first material isn't in its package, so it wasn't made again"); continue; }
                        var (b, p) = MakeVariation(tmp, fm.Export, a);
                        cur = b;
                        a.Path = p;
                        notes.Add($"{a.Name}: made again from the new model");
                    }
                    finally { try { File.Delete(tmp); } catch (IOException) { } }
                }
                return (cur, notes);
            });
            // the game's own variations edited here and living in this package (a build starts from the package before it)
            foreach (var ed in varState.Edits.Where(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase)))
            {
                string img = Path.Combine(VarFolder, ed.Image);
                if (!File.Exists(img)) continue;
                try { bytes = await Task.Run(() => MaterialVariations.ReplaceColorMap(bytes, ed.Material, img)); notes.Add($"{ed.Material.Split('.').Last()}: your edited color map put back"); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { notes.Add($"{ed.Material.Split('.').Last()}: your edit couldn't be put back ({ex.Message})"); }
            }
            if (notes.Any(n => n.EndsWith("from the new model") || n.EndsWith("put back")))
            {
                string outDir = Path.Combine(host.WorkFolder, "builds", "variations", "models");
                Directory.CreateDirectory(outDir);
                string outFile = Path.Combine(outDir, file);
                File.WriteAllBytes(outFile, bytes);
                host.SetPackage(file, outFile);
                if (built.ContainsKey(file)) built[file] = outFile;
            }
            foreach (string n in notes) Log("Variations: " + n);
            SaveVarState();
            // the classes whose lists name the model's own material or an added one: written again with the new paths
            if (Settings.Current.CookedFolder is string cooked)
            {
                var others = summonClasses.Value.GetValueOrDefault(file) ?? [];
                var classes = await Task.Run(() => MaterialVariations.ClassesFor(file, cooked, VarPath, others));
                foreach (var c in classes)
                    if (varState.Lists.TryGetValue(c.File, out var l) && l.Count > 0)
                        await WriteVariationList(c, file, l);
                    else if (c.Materials.Count > 0 && !varState.Lists.ContainsKey(c.File))
                        Log($"Note: {c.Label} picks one of {c.Materials.Count} materials at random each time it spawns ({string.Join(", ", c.Materials.Select(m => m.Split('.').Last()))}), over your model's: the Variations tab turns them off or makes your own.");
            }
        }
        catch (Exception ex) { VarFailed("Variations Not Made Again", $"After building {file}, the added variations couldn't be made again from the new model.", ex); }
    }

    /// <summary>An error of the Variations tab: in the log, and in a popup (Kurt, 2026-10-26).</summary>
    void VarFailed(string heading, string what, Exception ex, string? tip = null)
    {
        Log($"Variations: {what} {ex.Message}");
        string text = $"WHAT HAPPENED\n  • {what}\n  • {ex.Message}\n\nWHAT YOU CAN DO\n"
            + (tip != null ? $"  • {tip}\n" : "")
            + "  • Nothing in the mod changed for this step; the rest of the tab is as it was.\n"
            + "  • If it keeps happening, please report it on Discord with this message.";
        if (IsHandleCreated) BeginInvoke(() => Dialog.Show(this, text, heading, MessageBoxButtons.OK, MessageBoxIcon.Error));
    }

    /// <summary>A material tile: the color map, a tick in the corner, the name under it.</summary>
    sealed class VariationTile : Control
    {
        readonly float s;
        bool hot;
        public bool Checked { get; set; }
        public string Caption { get; set; } = "";
        public Image? Image { get; set; }

        public VariationTile(float scale)
        {
            s = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size((int)(88 * s), (int)(108 * s));
            Margin = new Padding(0, 0, (int)(6 * s), (int)(6 * s));
            Cursor = Cursors.Hand;
            MouseEnter += (_, _) => { hot = true; Invalidate(); };
            MouseLeave += (_, _) => { hot = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int side = Width - 2;
            var box = new Rectangle(1, 1, side - 1, side - 1);
            using (var bg = new SolidBrush(hot ? Ui.CardHover : Ui.Card)) g.FillRectangle(bg, box);
            if (Image != null) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(Image, Rectangle.Inflate(box, -(int)(4 * s), -(int)(4 * s))); }
            else TextRenderer.DrawText(g, "?", Ui.Bold(14f), box, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var pen = new Pen(Checked ? Ui.Accent : Ui.Line, Checked ? 2.5f * s : 1f)) g.DrawRectangle(pen, box);
            // the tick
            var tick = new RectangleF(box.Right - 20 * s, box.Y + 4 * s, 16 * s, 16 * s);
            using (var tb = new SolidBrush(Checked ? Ui.Accent : Color.FromArgb(160, 20, 20, 24))) g.FillEllipse(tb, tick);
            if (Checked)
            {
                using var tp = new Pen(Color.White, 2f * s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
                g.DrawLines(tp, [new PointF(tick.X + 4 * s, tick.Y + 8.5f * s), new PointF(tick.X + 7 * s, tick.Y + 11.5f * s), new PointF(tick.X + 12.5f * s, tick.Y + 5 * s)]);
            }
            TextRenderer.DrawText(g, Caption, Ui.Regular(8f), new Rectangle(0, side + (int)(2 * s), Width, Height - side - (int)(2 * s)), Checked ? Ui.Text : Ui.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); }
    }
}
