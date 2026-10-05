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
}
