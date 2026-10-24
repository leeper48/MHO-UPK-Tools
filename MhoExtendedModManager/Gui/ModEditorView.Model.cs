using MhoExtendedModManager.Model.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Model tab (2026-10-03, Kurt: the MHO MFF Importer inside the editor): an MFF model built onto one of the mod's
/// character packages. The tab is <see cref="ModelPage"/>; this side gives it the draft's packages and a work folder,
/// seeded from the mod's Model folder and copied back into it on Save (<see cref="ModDraft.ModelFolder"/>).
/// Only existing game packages are ever written (by Apply, as every mod package): nothing here touches game data.
/// </summary>
sealed partial class ModEditorView : IModelHost
{
    List<PartOffEntry> IModelHost.PartsOff => draft.PartsOff;
    ModelPage? modelPage;
    string? modelWork;

    Control ModelTabPage()
    {
        // Made when the tab first shows, after the editor is themed (as the Powers tab's 3D view), and themed itself then:
        // only its own controls, never the whole window.
        var host = new Panel { Dock = DockStyle.Fill };
        host.VisibleChanged += (_, _) =>
        {
            if (!host.Visible || modelPage != null) return;
            modelPage = new ModelPage(this) { Dock = DockStyle.Fill };
            host.Controls.Add(modelPage);
            Theme.ApplyTree(modelPage, Palette.Dark);
            Ui.Restyle(modelPage);
        };
        Disposed += (_, _) => { if (modelWork != null) try { Directory.Delete(modelWork, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
        return host;
    }

    // --- a model change not built yet (Kurt, 2026-10-06): leaving the Model tab, or Save Changes, asks to build it first ----------
    int modelTop = -1, lastTop;
    /// <summary>Build Now / Stay on Model after leaving the Editor: the main window shows the Editor again.</summary>
    public event Action? BackToEditor;
    bool askingModel;

    /// <summary>The Model tab has changes Build into Mod hasn't put into the mod yet.</summary>
    public bool ModelUnbuilt => modelPage?.HasUnbuiltChanges == true;

    void WatchModelLeave()
    {
        lastTop = tabs.SelectedIndex;
        tabs.SelectedChanged += i =>
        {
            int was = lastTop; lastTop = i;
            if (askingModel || was != modelTop || i == modelTop || !ModelUnbuilt) return;
            BeginInvoke(() => AskLeavingModel(i));
        };
    }

    /// <summary>Leaving the Model tab with a change not built: Build Now (back on the Model tab), Stay on Model, or leave it.</summary>
    async void AskLeavingModel(int goingTo)
    {
        if (askingModel || modelPage == null) return;
        askingModel = true;
        try
        {
            int pick = Dialog.Choose(this, modelPage.UnbuiltWhat + " Save Changes saves the mod without it until you build.", "Model Not Built", "Build Now", "Stay on Model", "Leave It Unbuilt");
            if (pick == 2) return;
            if (goingTo < 0) BackToEditor?.Invoke();
            tabs.Select(modelTop);
            if (pick == 0) await modelPage.BuildAsync();
        }
        finally { askingModel = false; }
    }

    /// <summary>Leaving the Editor (the main window's other tabs) while on the Model tab: the same question.</summary>
    public void LeavingEditor() { if (tabs.SelectedIndex == modelTop && ModelUnbuilt) BeginInvoke(() => AskLeavingModel(-1)); }

    /// <summary>Save Changes / Create Mod: with a model change not built, asks to build it first (Build and Save).</summary>
    async void SaveAsked()
    {
        if (askingModel) return;
        // no name yet (Kurt, 2026-10-17): to Overview → Details, the cursor in Name, before anything is built or saved
        if (nameBox.Text.Trim().Length == 0)
        {
            ShowTab((0, 0));
            Dialog.Show(this, "Give the mod a name first: Type it in Name, then save again.", "Name the Mod", MessageBoxButtons.OK, MessageBoxIcon.Information);
            nameBox.Focus();
            return;
        }
        if (modelPage != null && ModelUnbuilt)
        {
            askingModel = true;
            int pick;
            try { pick = Dialog.Choose(this, modelPage.UnbuiltWhat + " Build it into the mod before saving?", "Model Not Built", "Build and Save", "Cancel", "Save Without Building"); }
            finally { askingModel = false; }
            if (pick == 1) return;
            if (pick == 0)
            {
                askingModel = true;
                bool ok;
                try { tabs.Select(modelTop); ok = await modelPage.BuildAsync(); }
                finally { askingModel = false; }
                if (!ok) { Dialog.Show(this, "The build didn't finish, so nothing was saved: See the Model tab's log.", "Not Saved", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
        }
        if (!OwnColorsAtHome()) return;
        Save();
    }

    /// <summary>Test (--model-tab-test): the Model tab's page once it has shown.</summary>
    internal ModelPage? ModelPageForTest => modelPage;   // also Settings ▾ → Model (reload after a change)

    public string WorkFolder
    {
        get
        {
            if (modelWork != null) return modelWork;
            modelWork = Path.Combine(lib.DataFolder, "model-work-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(modelWork);
            if (editing != null && Path.Combine(editing.Folder, ModelWork.Folder) is var own && Directory.Exists(own)) ModelWork.CopyInto(own, modelWork);
            draft.ModelFolder = modelWork;   // Save copies it into the mod (Model\)
            return modelWork;
        }
    }

    IReadOnlyList<(string File, string Path)> IModelHost.Packages => draft.Packages;

    public string? SavedPath(string file) =>
        editing != null && editing.Manifest.UpkReplacements.FirstOrDefault(f => f.Equals(file, StringComparison.OrdinalIgnoreCase)) is string f
            ? Path.Combine(editing.Folder, f) : null;

    public void SetPackage(string file, string path)
    {
        int k = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (k >= 0) draft.Packages[k] = (draft.Packages[k].File, path); else draft.Packages.Add((file, path));
        RefreshPackages();
        _ = AfterModelBuild(file, path);
    }

    public void AddPackage(string file, string path)
    {
        int k = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (k >= 0) draft.Packages[k] = (draft.Packages[k].File, path); else draft.Packages.Add((file, path));
        RefreshPackages();
    }

    /// <summary>
    /// A Model tab build handed in (Kurt, 2026-10-23: an elbow fixed in Blender showed in the Model tab, not in the Powers
    /// tab, and Save would have dropped it). A build starts from the stock package (or the one kept before the model), so the
    /// Animations tab's changes of that package are applied to it again; when borrowed powers change the package too, that
    /// result becomes the package the borrows start from (Model\borrow_base, which they rebuild from on every change and on
    /// Save: they didn't see a build made after the first borrow) and the borrows are built again; the added props go back in.
    /// </summary>
    async Task AfterModelBuild(string file, string path)
    {
        if (game == null) return;
        var swaps = draft.AnimSwaps.Where(s => s.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        string kept = Path.Combine(WorkFolder, "borrow_base", file);
        bool borrowed = File.Exists(kept);
        if (swaps.Count == 0 && !borrowed) return;
        var log = new List<string>();
        byte[]? bytes;
        string cooked = game.Cooked;
        Mod? mod = editing;
        try { bytes = await Task.Run(() => ReapplySwaps(file, path, swaps, mod, cooked, log)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException or ArgumentException)
        {
            Dialog.Show(this, $"The model is built, but this package's animation changes (Animations tab) couldn't be put back on it: {ex.Message}\n\nChange them again in the Animations tab before saving.", "Animation Changes Not Kept", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (IsDisposed || DraftPath(file) is not string now || !now.Equals(path, StringComparison.OrdinalIgnoreCase)) return;   // (another build came meanwhile)
        if (bytes != null) UseWorkCopy(file, bytes);
        ModelSyncLog = log;
        if (borrowed && DraftPath(file) is string withSwaps)
        {
            File.Copy(withSwaps, kept, true);   // (the package before the borrows: the model as built, the animation changes on it)
            RebuildBorrows();                   // (then the added props: ReapplyProps)
        }
        else if (draft.AddedProps.Any(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase))) await ApplyPropsTo(file);
    }

    /// <summary>What the last Model tab build's sync did (tests).</summary>
    internal List<string> ModelSyncLog { get; private set; } = [];

    /// <summary>The Animations tab's changes of <paramref name="file"/> (AnimSwaps) applied to the package at
    /// <paramref name="path"/>: earlier copies of those slots taken out first, then each donor's animations copied in as the
    /// tab does (turned to this skeleton's rest). Null when there's nothing to do.</summary>
    static byte[]? ReapplySwaps(string file, string path, List<AnimSwapEntry> swaps, Mod? mod, string cooked, List<string> log)
    {
        if (swaps.Count == 0) return null;
        var files = CostumeAnims.FilesFor(mod, cooked);
        var ca = CostumeAnims.Read(path, file, files) ?? throw new InvalidDataException($"{file}'s animation list doesn't read");
        string cls = ca.Class;
        var inherited = ca.Inherited ? ca.Sets.Select(x => x.Path).ToList() : null;
        string tmp = Path.Combine(Path.GetTempPath(), $"mhoextmm_swaps_{Guid.NewGuid():N}_{file}");
        try
        {
            byte[] cur = File.ReadAllBytes(path);
            if (AnimSwap.Unswap(MhoPackageModifier.Package.FromBytes(cur), cls, swaps.Select(s => s.Slot), log) is { } un) cur = un;
            foreach (var g in swaps.GroupBy(s => s.Donor, StringComparer.OrdinalIgnoreCase))
            {
                if (!g.Key.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)) { log.Add($"{string.Join(", ", g.Select(s => s.Slot))}: from the mod \"{g.Key}\", not put back (change them again in the Animations tab)"); continue; }
                string donorPath = StockFiles.For(cooked, g.Key);
                var dca = CostumeAnims.Read(donorPath, g.Key, CostumeAnims.FilesFor(null, cooked)) ?? throw new InvalidDataException($"{g.Key}'s animations don't read");
                var list = new List<AnimSwap.Swap>();
                string? from = null;
                foreach (var s in g)
                {
                    var pick = dca.Anims.FirstOrDefault(a => a.Name.Equals(s.Animation, StringComparison.OrdinalIgnoreCase));
                    if (pick?.From.File == null) { log.Add($"{s.Slot}: {g.Key} has no {s.Animation}, left as the game's"); continue; }
                    list.Add(new AnimSwap.Swap(s.Slot, pick.From.File, pick.From.Export, pick.Export));
                    from ??= pick.From.File;
                }
                if (list.Count == 0) continue;
                // one copy step per use of a source animation (two slots playing the same one: Kurt's idle and idle_combat both
                // Thor's idle; in one step the second dropped the first)
                Dictionary<string, System.Numerics.Quaternion>? turns = null;
                while (list.Count > 0)
                {
                    var batch = list.GroupBy(x => (x.DonorFile, x.DonorSet, x.DonorSequence)).Select(x => x.First()).ToList();
                    foreach (var x in batch) list.Remove(x);
                    File.WriteAllBytes(tmp, cur);
                    turns ??= RestTurnsFor(from!, tmp);
                    cur = AnimSwap.Build(tmp, cls, batch, log, inherited, retarget: turns);
                    log.Add($"{string.Join(", ", batch.Select(x => x.Slot))}: {Path.GetFileNameWithoutExtension(g.Key)}'s again, on the new build");
                }
            }
            if (AnimSwap.DropEmptySets(MhoPackageModifier.Package.FromBytes(cur), cls, log) is { } tidy) cur = tidy;
            return cur;
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    public void RemovePackage(string file)
    {
        draft.Packages.RemoveAll(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        RefreshPackages();
    }
}
