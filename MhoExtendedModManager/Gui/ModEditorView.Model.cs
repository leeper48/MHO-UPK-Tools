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
                if (!ok) { Dialog.Show(this, "The build didn't finish, so nothing was saved: see the Model tab's log.", "Not Saved", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
        }
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
    }

    public void AddPackage(string file, string path) => SetPackage(file, path);

    public void RemovePackage(string file)
    {
        draft.Packages.RemoveAll(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        RefreshPackages();
    }
}
