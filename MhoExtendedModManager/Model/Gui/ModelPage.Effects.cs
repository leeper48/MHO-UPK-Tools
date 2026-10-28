using System.Text.Json;
using MhoExtendedModManager.Gui;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Effect Models (Kurt, 2026-10-25: It's Raining Rodents' falling squirrels are particles): a hero's power effect static meshes
/// (<see cref="EffectModels"/>) listed by hero, shown in 3D, edited in Blender (Ctrl+S sends the edit back) or replaced by an
/// FBX, and built into every package holding a copy of the mesh (the game shows the copy it loads first). The edits are kept in
/// the work folder's effects\ (effects.json: mesh path → the edited FBX, the Blender folder, the last save taken in, the
/// packages built and the ones this view added), which Save copies into the mod's Model folder.
/// </summary>
sealed partial class ModelPage
{
    sealed class EffectEdit
    {
        public string? Fbx { get; set; }
        public string? Folder { get; set; }
        public int Applied { get; set; }
        public bool Built { get; set; }
        public List<string> Added { get; set; } = [];
        /// <summary>The packages to build into, as ticked under Packages (null: the default, <see cref="DefaultPackages"/>).</summary>
        public List<string>? Packages { get; set; }
    }

    /// <summary>The packages a build goes into by default: every effect package (powers, hotspots, projectiles, conditions) and
    /// the hero's own character packages (they load whenever the hero is played, so their copy would win); another character's
    /// package (an enemy, an NPC, another hero) only when ticked, since a copy there changes that character's look too.</summary>
    static List<string> DefaultPackages(EffectModels.Entry e) =>
        e.Packages.Where(p => EffectModels.EffectPackage(p) || EffectModels.HeroOwns(p, e.Hero)).ToList();

    List<string> ChosenPackages(EffectModels.Entry e) =>
        effectEdits.TryGetValue(e.Path, out var ed) && ed.Packages is { } list ? e.Packages.Where(p => list.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList() : DefaultPackages(e);

    Control? effectsView;
    readonly List<Label> viewTabs = [];
    readonly CharacterList effectList = new() { Dock = DockStyle.Fill };
    readonly TextBox effectFilter = new();
    MeshViewer effectViewer = null!;
    readonly Label effectTitle = new() { AutoSize = true, Font = Ui.Bold(11f), ForeColor = Ui.Text, Margin = new Padding(2, 0, 0, 2) };
    readonly Label effectInfo = new() { AutoSize = true, ForeColor = Ui.Subtle, Margin = new Padding(2, 0, 0, 6) };
    readonly Label effectStatus = new() { AutoSize = true, ForeColor = Ui.Subtle, Margin = new Padding(2, 6, 0, 0) };
    readonly CheckedListBox effectCopies = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false, CheckOnClick = true };
    bool fillingCopies;
    Button effectEdit = null!, effectImport = null!, effectBuild = null!, effectForget = null!;
    readonly CheckBox effectBuildOnSave = new() { Text = "Build When Blender Saves", AutoSize = true, Margin = new Padding(2, 6, 0, 0) };
    readonly HashSet<string> effectOpen = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, EffectEdit> effectEdits = new(StringComparer.OrdinalIgnoreCase);
    EffectModels.Entry? effectCurrent;
    FileSystemWatcher? effectWatch;
    readonly System.Windows.Forms.Timer effectDelay = new() { Interval = 700 };
    int effectRequest;
    bool effectBusy;

    string EffectsFolder => Path.Combine(host.WorkFolder, "effects");
    string EffectsFile => Path.Combine(EffectsFolder, "effects.json");

    /// <summary>The Model tab's two views, as flat tabs at the left of its top bar (Kurt, 2026-10-26: a tab, not a button):
    /// Characters (models fitted onto a character) and Effects (power effects' models).</summary>
    Control ViewTabs()
    {
        var strip = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var (title, tip) in new[]
        {
            ("Characters", "Models fitted onto a character: an MFF model, an FBX or a model from the game, built onto a hero, team-up, NPC, summon or prop. The only view where a click on a source character makes it the source."),
            ("Props", "Props for the costume picked under Target: pick a character on the left (a game costume, an MFF character, an FBX) and click one of its props in the list under it to add it. The target never changes here."),
            ("Effects", "Power effects' models: the meshes a hero's powers draw as particles or carry on a projectile (Squirrel Girl's falling squirrels, acorns …). Pick one, change it in Blender or with an FBX, and Build into Mod puts it into every package that holds it."),
        })
        {
            var tab = new Label { Text = title, AutoSize = true, Padding = new Padding(8, 4, 8, 6), Cursor = Cursors.Hand, Font = Ui.Bold(9.75f), ForeColor = viewTabs.Count == 0 ? Ui.Text : Ui.Subtle, BackColor = Color.Transparent, Margin = new Padding(0) };
            int index = viewTabs.Count;
            tab.Click += (_, _) => SetView(index);
            tab.Paint += (_, e) =>
            {
                bool on = index == CurrentView;
                if (!on) return;
                using var b = new SolidBrush(Ui.Accent);
                float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
                e.Graphics.FillRectangle(b, 6 * s, tab.Height - 3 * s, tab.Width - 12 * s, 2.5f * s);
            };
            Ui.Tip(tab, tip);
            viewTabs.Add(tab);
            strip.Controls.Add(tab);
        }
        return strip;
    }

    void ToggleEffects() => ShowView(effectsView == null || !effectsView.Visible);

    /// <summary>The view shown: 0 Characters, 1 Props, 2 Effects.</summary>
    int CurrentView => effectsView?.Visible == true ? 2 : propMode ? 1 : 0;

    void SetView(int view)
    {
        ShowView(view == 2);
        if (view != 2) SetPropMode(view == 1);
        for (int k = 0; k < viewTabs.Count; k++) { viewTabs[k].ForeColor = k == CurrentView ? Ui.Text : Ui.Subtle; viewTabs[k].Invalidate(); }
    }

    void ShowView(bool on)
    {
        if (on == (effectsView?.Visible == true)) return;
        if (on && effectsView == null)
        {
            effectsView = BuildEffectsView();
            Theme.ApplyTree(effectsView, Palette.Dark);
            Ui.Restyle(effectsView);
            LoadEffectEdits();
            FillEffects();
        }
        // the characters' body and this view take turns in the same cell
        layoutRoot.SuspendLayout();
        layoutRoot.Controls.Remove(on ? layoutBody : effectsView!);
        layoutRoot.Controls.Add(on ? effectsView! : layoutBody, 0, 1);
        effectsView!.Visible = on;
        layoutRoot.ResumeLayout();
        for (int k = 0; k < viewTabs.Count; k++) { viewTabs[k].ForeColor = k == CurrentView ? Ui.Text : Ui.Subtle; viewTabs[k].Invalidate(); }
    }

    Control BuildEffectsView()
    {
        effectViewer = new MeshViewer { Dock = DockStyle.Fill, Compact = true, Background = Color.FromArgb(28, 30, 38) };
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.Transparent, Padding = new Padding(6) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        SearchBox.AddClear(effectFilter);
        Ui.Tip(effectFilter, "Filter the effect models by hero or name.");
        effectFilter.TextChanged += (_, _) => FillEffects();
        body.Controls.Add(Column("EFFECT MODELS", effectFilter, effectList), 0, 0);
        body.Controls.Add(Column("PREVIEW", null, effectViewer), 1, 0);

        effectEdit = Ui.FlatButton("Edit in Blender", EffectEditInBlender, "Opens this model in Blender (the first time it's exported with its textures; after that the same scene opens again). Each Ctrl+S there sends it back here: the preview shows it as it will be built.");
        effectImport = Ui.FlatButton("Import FBX", EffectImportFbx, "Takes an FBX (or OBJ, DAE …) as this model: its parts go to the mesh's sections by material name, or all into its one section. The preview shows it as it will be built.");
        effectBuild = Ui.AccentButton("Build into Mod", () => _ = EffectBuildAsync(), "Puts the edited model into every package listed under Packages (each added to the mod when it isn't in it yet). Save Changes keeps them; Apply Changes puts them into the game.");
        effectForget = Ui.FlatButton("Back to the Game's", EffectForget, "Forgets this model's edit; the packages this view added to the mod for it leave the mod (packages the mod had already keep the built model until you build them again or remove them).");
        Ui.TipTitled(effectBuildOnSave, "Build When Blender Saves", "Each Ctrl+S in Blender also builds the model into the mod.");
        effectBuildOnSave.CheckedChanged += (_, _) => SaveEffectEdits();
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        foreach (var b in new[] { effectEdit, effectImport, effectBuild, effectForget }) { b.Margin = new Padding(0, 0, 6, 6); buttons.Controls.Add(b); }
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = Color.Transparent, Margin = new Padding(4) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var _ in Enumerable.Range(0, 3)) right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var _ in Enumerable.Range(0, 3)) right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(effectTitle, 0, 0);
        right.Controls.Add(effectInfo, 0, 1);
        right.Controls.Add(new Label { Text = "PACKAGES", AutoSize = true, ForeColor = Ui.Subtle, Font = Ui.Bold(8.5f), Margin = new Padding(2, 2, 0, 4) }, 0, 2);
        effectCopies.BackColor = Color.FromArgb(28, 30, 38); effectCopies.ForeColor = Ui.Text;
        Ui.Tip(effectCopies, "Every package with a copy of this model under the same path: the game shows whichever it loads first, so a build goes into each ticked one. Effect packages and the hero's own packages are ticked; another character's (an enemy, an NPC, another hero) only if you tick it, since its copy changes that character's look too.");
        effectCopies.ItemCheck += (_, ev) =>
        {
            if (fillingCopies || effectCurrent is not { } cur) return;
            var ed = EditFor(cur);
            var now = ChosenPackages(cur).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string file = cur.Packages[ev.Index];
            if (ev.NewValue == CheckState.Checked) now.Add(file); else now.Remove(file);
            ed.Packages = cur.Packages.Where(now.Contains).ToList();
            ed.Built = false;
            SaveEffectEdits();
            BeginInvoke(UpdateEffectButtons);
        };
        right.Controls.Add(effectCopies, 0, 3);
        right.Controls.Add(buttons, 0, 4);
        right.Controls.Add(effectBuildOnSave, 0, 5);
        right.Controls.Add(effectStatus, 0, 6);
        body.Controls.Add(right, 2, 0);
        // the labels wrap at the column's width (a fixed cap broke the mesh path mid-word at 200 %)
        right.Resize += (_, _) => { foreach (var l in new[] { effectInfo, effectStatus }) l.MaximumSize = new Size(Math.Max(100, right.ClientSize.Width - 12), 0); };

        effectList.Thumb = it => it.Header ? Thumbs.BaseHero($"UC__MarvelPlayer_{it.Key["hero:".Length..]}_SF.upk") : null;
        effectList.MouseUp += (_, e) =>
        {
            int i = effectList.IndexFromPoint(e.Location);
            if (i >= 0 && i < effectList.Items.Count && effectList.Items[i] is CharacterList.Item { Header: true } h)
            { string hero = h.Key["hero:".Length..]; if (!effectOpen.Add(hero)) effectOpen.Remove(hero); FillEffects(); }
        };
        effectList.SelectedIndexChanged += (_, _) =>
        {
            if (effectList.SelectedItem is CharacterList.Item { Header: false } it && EffectModels.All.FirstOrDefault(x => x.Key() == it.Key) is { } e) ShowEffect(e);
        };
        effectDelay.Tick += (_, _) => { effectDelay.Stop(); ApplyEffectSync(live: true); };
        Disposed += (_, _) => { effectWatch?.Dispose(); effectDelay.Dispose(); };
        UpdateEffectButtons();
        return body;
    }

    void FillEffects()
    {
        string f = effectFilter.Text.Trim();
        string? keep = effectCurrent?.Key();
        effectList.BeginUpdate(); effectList.Items.Clear();
        var shown = EffectModels.All.Where(e => f.Length == 0 || $"{e.Hero} {e.Name} {e.Path}".Contains(f, StringComparison.OrdinalIgnoreCase));
        foreach (var g in shown.GroupBy(e => e.Hero, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            int edited = g.Count(e => effectEdits.ContainsKey(e.Path));
            bool open = f.Length > 0 || effectOpen.Contains(g.Key) || g.Any(e => e.Key() == keep);
            effectList.Items.Add(new CharacterList.Item("hero:" + g.Key, g.Key, $"{g.Count()} effect model{(g.Count() == 1 ? "" : "s")}{(edited > 0 ? $" · {edited} edited" : "")}", Header: true, Expanded: open));
            if (!open) continue;
            foreach (var e in g.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                int heroes = EffectModels.All.Count(x => x.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase));
                string state = effectEdits.TryGetValue(e.Path, out var ed) ? (ed.Built ? " · Built" : " · Edited") : "";
                effectList.Items.Add(new CharacterList.Item(e.Key(), e.Name, $"{e.Vertices:N0} vertices · {e.Packages.Count} package{(e.Packages.Count == 1 ? "" : "s")}{(heroes > 1 ? $" · shared by {heroes} heroes" : "")}{state}", Indent: 1));
            }
        }
        effectList.EndUpdate();
        if (keep != null) Reselect(effectList, keep);
    }

    void ShowEffect(EffectModels.Entry e)
    {
        effectCurrent = e;
        var sharers = EffectModels.All.Where(x => x.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase)).Select(x => x.Hero).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        effectTitle.Text = e.Name;
        effectInfo.Text = $"{e.Path} · {e.Vertices:N0} vertices, {e.Sections} section{(e.Sections == 1 ? "" : "s")}" + (sharers.Count > 1 ? $"\nShared by {sharers.Count} heroes ({string.Join(", ", sharers.Take(6))}{(sharers.Count > 6 ? " …" : "")}): a change shows on all of them." : "");
        effectInfo.ForeColor = sharers.Count > 1 ? Color.FromArgb(240, 190, 90) : Ui.Subtle;
        fillingCopies = true;
        effectCopies.Items.Clear();
        var chosen = ChosenPackages(e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string p in e.Packages)
        {
            string tag = EffectModels.EffectPackage(p) ? "" : EffectModels.HeroOwns(p, e.Hero) ? "   · the hero's own" : "   · another character";
            effectCopies.Items.Add(p + tag + (host.Packages.Any(x => x.File.Equals(p, StringComparison.OrdinalIgnoreCase)) ? "   (in the mod)" : ""), chosen.Contains(p));
        }
        fillingCopies = false;
        WatchEffect();
        PreviewEffect();
        UpdateEffectButtons();
    }

    void UpdateEffectButtons()
    {
        bool any = effectCurrent != null && !effectBusy;
        var ed = effectCurrent != null && effectEdits.TryGetValue(effectCurrent.Path, out var x) ? x : null;
        effectEdit.Enabled = effectImport.Enabled = any;
        effectBuild.Enabled = any && ed?.Fbx != null;
        effectForget.Enabled = any && ed != null;
        effectBuildOnSave.Visible = ed?.Folder != null;
        effectStatus.Text = effectCurrent == null ? "Pick an effect model on the left." : ed == null ? "The game's model." :
            $"{(ed.Fbx != null ? "Edited" : "Opened in Blender")}{(ed.Applied > 0 ? $" (Blender save {ed.Applied} taken in)" : "")} · {(ed.Built ? "Built into the mod" : ed.Fbx != null ? "Not built yet: Build into Mod" : "Ctrl+S in Blender sends it back")}";
        effectStatus.ForeColor = ed?.Built == true ? Ui.Enabled : Ui.Subtle;
    }

    // --- preview ------------------------------------------------------------------------------------------------------------
    /// <summary>The model as it will be built (the edit built into the first package, in memory), else the game's.</summary>
    async void PreviewEffect()
    {
        if (effectCurrent is not { } e || Settings.Current.CookedFolder is not string cooked) return;
        int id = ++effectRequest;
        string? fbx = effectEdits.TryGetValue(e.Path, out var ed) && ed.Fbx != null ? Path.Combine(EffectsFolder, ed.Fbx) : null;
        effectViewer.ShowMessage("Loading…");
        var result = await Task.Run(() =>
        {
            string? pkgPath = EffectModels.StockPackage(e, cooked);
            if (pkgPath == null) return ((StaticMesh?)null, (MeshViewer.Tex?[]?)null, "", (string?)"its packages aren't in the game folder");
            try
            {
                var pkg = Package.Open(pkgPath);
                int index = EffectModels.Find(pkg, e.Path);
                if (index < 0) return ((StaticMesh?)null, (MeshViewer.Tex?[]?)null, "", (string?)$"{e.Path} isn't in {Path.GetFileName(pkgPath)}");
                StaticMesh sm = StaticMesh.Read(pkg, pkg.Exports[index]);
                string note = "";
                if (fbx != null && File.Exists(fbx))
                {
                    EffectModels.Build(pkgPath, e.Path, fbx, out sm, out var notes);
                    note = notes.Count > 0 ? string.Join("; ", notes) : "";
                }
                return ((StaticMesh?)sm, (MeshViewer.Tex?[]?)EffectModels.PreviewTextures(pkg, sm, cooked), note, (string?)null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException or Assimp.AssimpException)
            { return ((StaticMesh?)null, (MeshViewer.Tex?[]?)null, "", (string?)ex.Message); }
        });
        if (id != effectRequest || IsDisposed) return;
        var (mesh, tex, info, error) = result;
        if (mesh == null) { effectViewer.ShowMessage(error ?? "can't be shown"); if (error != null) Log($"Effect model {e.Name}: {error}"); return; }
        var tri = new int[mesh.Indices.Length / 3];
        for (int s = 0; s < mesh.Sections.Length; s++)
            for (int k = 0; k < mesh.Sections[s].NumTriangles; k++) if (mesh.Sections[s].FirstIndex / 3 + k < tri.Length) tri[mesh.Sections[s].FirstIndex / 3 + k] = s;
        effectViewer.ShowMesh(e.Name, mesh.Positions, mesh.Normals, mesh.TexCoords.Length > 0 ? mesh.TexCoords[0] : [], [.. mesh.Indices.Select(i => (int)i)], tri, tex ?? [],
            $"{(fbx != null ? "edited" : "the game's")} · {mesh.Positions.Length:N0} vertices, {mesh.Indices.Length / 3:N0} triangles{(info.Length > 0 ? " · " + info : "")}");
    }

    // --- Blender, FBX ----------------------------------------------------------------------------------------------------------
    void EffectEditInBlender()
    {
        if (effectCurrent is not { } e || Settings.Current.CookedFolder is not string cooked) return;
        var ed = EditFor(e);
        if (ed.Folder != null && File.Exists(Path.Combine(ed.Folder, "model.blend")))
        {
            if (BlenderLaunch.Reopen(ed.Folder) is string why) { Log("Blender: " + why); return; }
            Log($"Blender: opening {e.Name}'s scene again ({Path.Combine(ed.Folder, "model.blend")}). Each Ctrl+S there sends it back here.");
            WatchEffect();
            return;
        }
        string? stock = EffectModels.StockPackage(e, cooked);
        if (stock == null) { Log($"{e.Name}: its packages aren't in the game folder."); return; }
        string folder = UniqueDir(Path.Combine(Settings.Home, "effects", $"{e.Hero} {FbxExport.SafeName(e.Name)}"));
        try { EffectModels.Export(stock, e.Path, folder); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { Log($"{e.Name}: {ex.Message}"); return; }
        if (BlenderLaunch.OpenStatic(folder) is string why2) { Log("Blender: " + why2); return; }
        ed.Folder = folder; ed.Applied = 0;
        SaveEffectEdits();
        Log($"Blender: {e.Name} exported to {folder} and opening there. Change it (or put another model in its place) and press Ctrl+S: it comes back here.");
        WatchEffect();
        UpdateEffectButtons();
    }

    void EffectImportFbx()
    {
        if (effectCurrent is not { } e) return;
        using var d = new OpenFileDialog { Title = $"A Model for {e.Name}", Filter = "Models (*.fbx;*.obj;*.dae)|*.fbx;*.obj;*.dae|All files (*.*)|*.*" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        TakeEffectFbx(e, d.FileName, $"{Path.GetFileName(d.FileName)} is {e.Name} now");
    }

    /// <summary>An FBX as the model's edit: kept in the work folder, previewed (which also tells whether it builds).</summary>
    void TakeEffectFbx(EffectModels.Entry e, string file, string what)
    {
        var ed = EditFor(e);
        string dir = Path.Combine(EffectsFolder, FbxExport.SafeName(e.Path));
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        string name = $"edit_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(file).ToLowerInvariant()}";
        File.Copy(file, Path.Combine(dir, name), true);
        ed.Fbx = Path.Combine(FbxExport.SafeName(e.Path), name);
        ed.Built = false;
        SaveEffectEdits();
        Log($"Effect model: {what}. Build into Mod puts it into {e.Packages.Count} package{(e.Packages.Count == 1 ? "" : "s")}.");
        FillEffects(); PreviewEffect(); UpdateEffectButtons();
    }

    void WatchEffect()
    {
        effectWatch?.Dispose(); effectWatch = null;
        if (effectCurrent is not { } e || !effectEdits.TryGetValue(e.Path, out var ed) || ed.Folder == null || !Directory.Exists(ed.Folder)) return;
        string from = Path.Combine(ed.Folder, "from_blender");
        try
        {
            Directory.CreateDirectory(from);
            effectWatch = new FileSystemWatcher(from, "sync.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler on = (_, _) => Later(() => { effectDelay.Stop(); effectDelay.Start(); });
            effectWatch.Changed += on; effectWatch.Created += on;
            effectWatch.Renamed += (_, _) => Later(() => { effectDelay.Stop(); effectDelay.Start(); });
            effectWatch.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Log($"Blender: can't watch {from}: {ex.Message}"); return; }
        ApplyEffectSync(live: false);   // (a save made while this wasn't shown)
    }

    void ApplyEffectSync(bool live)
    {
        if (effectCurrent is not { } e || !effectEdits.TryGetValue(e.Path, out var ed) || ed.Folder == null) return;
        string file = Path.Combine(ed.Folder, "from_blender", "sync.json"), fbx = Path.Combine(ed.Folder, "from_blender", "model.fbx");
        if (!File.Exists(file) || !File.Exists(fbx)) return;
        int version = 0;
        for (int i = 0; i < 5; i++)
            try { version = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("version").GetInt32(); break; }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { Thread.Sleep(150); }
        if (version <= ed.Applied) return;
        ed.Applied = version;
        TakeEffectFbx(e, fbx, $"Blender sent {e.Name} (save {version})");
        if (live && effectBuildOnSave.Checked) _ = EffectBuildAsync();
    }

    // --- build ------------------------------------------------------------------------------------------------------------------
    async Task<bool> EffectBuildAsync()
    {
        if (effectCurrent is not { } e || !effectEdits.TryGetValue(e.Path, out var ed) || ed.Fbx == null || effectBusy || Settings.Current.CookedFolder is not string cooked) return false;
        string fbx = Path.Combine(EffectsFolder, ed.Fbx);
        var chosen = ChosenPackages(e);
        if (chosen.Count == 0) { Log($"{e.Name}: no package is ticked under Packages."); return false; }
        effectBusy = true; UpdateEffectButtons();
        Log($"Building {e.Name} into {chosen.Count} package{(chosen.Count == 1 ? "" : "s")}{(chosen.Count < e.Packages.Count ? $" ({e.Packages.Count - chosen.Count} other character copies left as the game's: tick them under Packages to change them too)" : "")}:");
        int done = 0;
        using (Busy.Begin($"Model: building {e.Name}"))
            foreach (string file in chosen)
            {
                var have = host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                string stock = StockFiles.For(cooked, file);
                string start = have.Path != null && File.Exists(have.Path) ? have.Path : stock;
                if (!File.Exists(start)) { Log($"  {file}: not in the game folder"); continue; }
                try
                {
                    string outDir = Path.Combine(host.WorkFolder, "builds", "effects", FbxExport.SafeName(e.Name));
                    Protected.CheckWrite(outDir);
                    Directory.CreateDirectory(outDir);
                    string outFile = Path.Combine(outDir, file);
                    var notes = await Task.Run(() =>
                    {
                        byte[] bytes = EffectModels.Build(start, e.Path, fbx, out _, out var n);
                        File.WriteAllBytes(outFile, bytes);
                        return n;
                    });
                    if (have.Path == null) { host.AddPackage(file, stock); if (!ed.Added.Contains(file, StringComparer.OrdinalIgnoreCase)) ed.Added.Add(file); }
                    host.SetPackage(file, outFile);
                    done++;
                    Log($"  {file}: built, verified{(have.Path == null ? " (added to the mod)" : "")}{(notes.Count > 0 ? " · " + string.Join("; ", notes) : "")}");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException or Assimp.AssimpException)
                { Log($"  {file}: {ex.Message}"); }
            }
        ed.Built = done == chosen.Count;
        SaveEffectEdits();
        effectBusy = false;
        Log(ed.Built ? $"Done: {e.Name} is in the mod ({done} packages). Save Changes keeps it; Apply Changes puts it into the game." : $"{e.Name}: {done} of {chosen.Count} packages built (see above).");
        ShowEffect(e); FillEffects();
        return ed.Built;
    }

    void EffectForget()
    {
        if (effectCurrent is not { } e || !effectEdits.TryGetValue(e.Path, out var ed)) return;
        var stillUsed = effectEdits.Where(kv => !kv.Key.Equals(e.Path, StringComparison.OrdinalIgnoreCase)).SelectMany(kv => kv.Value.Added).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ed.Added.Where(f => !stillUsed.Contains(f) && !built.ContainsKey(f))) { host.RemovePackage(file); Log($"{file} left the mod."); }
        effectEdits.Remove(e.Path);
        SaveEffectEdits();
        Log($"{e.Name} is the game's again{(ed.Added.Count < e.Packages.Count && ed.Built ? " (packages the mod already had keep the built model until they're built again or removed)" : "")}.");
        ShowEffect(e); FillEffects();
    }

    /// <summary>
    /// Test (--model-effects-test): the Effect Models view through its own code: opened, the model picked from the list, the
    /// preview, an FBX taken in (the one an --effect-roundtrip Blender save sent), Build into Mod into every package, then Back
    /// to the Game's. MHO_TEST_EFFECTSHOT=&lt;png&gt;: the view after the build.
    /// </summary>
    internal async Task<int> TestEffects(string hero, string path, string fbx, Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        ToggleEffects();
        Check(effectsView?.Visible == true && effectsView.Parent == layoutRoot && layoutBody.Parent == null, "Effect Models shows in place of the characters");
        var e = EffectModels.All.FirstOrDefault(x => x.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase) && x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        Check(e != null, $"{hero} has {path} ({EffectModels.All.Count(x => x.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase))} effect models)");
        if (e == null) return fails;
        effectFilter.Text = e.Name;
        int row = effectList.Items.OfType<CharacterList.Item>().ToList().FindIndex(r => r.Key == e.Key());
        Check(row >= 0, "the filter shows it under its hero");
        effectList.SelectedIndex = row;
        for (int i = 0; i < 100 && !effectViewer.HasMesh; i++) await Task.Delay(100);
        Check(effectViewer.HasMesh && effectCopies.Items.Count == e.Packages.Count && effectCopies.CheckedItems.Count == DefaultPackages(e).Count, $"the preview shows it and its {e.Packages.Count} packages are listed ({effectCopies.CheckedItems.Count} ticked)");
        TakeEffectFbx(e, fbx, "test FBX");
        for (int i = 0; i < 100 && !effectBuild.Enabled; i++) await Task.Delay(100);
        bool ok = await EffectBuildAsync();
        var want = DefaultPackages(e);
        var inMod = want.Where(p => host.Packages.Any(x => x.File.Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
        Check(ok && inMod.Count == want.Count, $"Build into Mod puts it into every ticked package ({inMod.Count} of {want.Count} in the mod)");
        if (inMod.Count > 0)
        {
            string built0 = host.Packages.First(x => x.File.Equals(inMod[0], StringComparison.OrdinalIgnoreCase)).Path;
            var bp = Package.Open(built0);
            int bi = EffectModels.Find(bp, e.Path);
            Check(bi >= 0 && StaticMesh.Read(bp, bp.Exports[bi]).Positions.Length > 0, $"{inMod[0]} in the mod reads back with the mesh");
        }
        if (Environment.GetEnvironmentVariable("MHO_TEST_EFFECTSHOT") is { Length: > 0 } shot)
        {
            await Task.Delay(800);
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(shot);
            say("snapshot: " + shot);
        }
        var addedNow = effectEdits[e.Path].Added.ToList();
        EffectForget();
        Check(!effectEdits.ContainsKey(e.Path) && addedNow.All(p => !host.Packages.Any(x => x.File.Equals(p, StringComparison.OrdinalIgnoreCase))), $"Back to the Game's takes the {addedNow.Count} packages it added out of the mod");
        ToggleEffects();
        Check(layoutBody.Parent == layoutRoot && effectsView?.Parent == null, "clicked again: the characters");
        return fails;
    }

    EffectEdit EditFor(EffectModels.Entry e) => effectEdits.TryGetValue(e.Path, out var ed) ? ed : effectEdits[e.Path] = new EffectEdit();

    void LoadEffectEdits()
    {
        try
        {
            if (File.Exists(EffectsFile))
            {
                var doc = JsonSerializer.Deserialize<EffectsState>(File.ReadAllText(EffectsFile));
                effectEdits = new Dictionary<string, EffectEdit>(doc?.Edits ?? [], StringComparer.OrdinalIgnoreCase);
                effectBuildOnSave.Checked = doc?.BuildOnSave ?? false;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { Log("Effect models: " + ex.Message); }
    }

    void SaveEffectEdits()
    {
        try
        {
            Protected.CheckWrite(EffectsFolder);
            Directory.CreateDirectory(EffectsFolder);
            File.WriteAllText(EffectsFile, JsonSerializer.Serialize(new EffectsState { Edits = new(effectEdits), BuildOnSave = effectBuildOnSave.Checked }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex) { Log("Effect models: " + ex.Message); }
    }

    sealed class EffectsState
    {
        public Dictionary<string, EffectEdit> Edits { get; set; } = [];
        public bool BuildOnSave { get; set; }
    }
}

static class EffectEntryKey
{
    /// <summary>The list key of an effect model (hero and path: a shared mesh is listed under each hero).</summary>
    public static string Key(this EffectModels.Entry e) => "effect:" + e.Hero + "|" + e.Path;
}
