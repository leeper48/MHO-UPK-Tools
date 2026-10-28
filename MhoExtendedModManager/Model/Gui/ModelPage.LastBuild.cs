using System.Text.Json;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Revert to Original and Revert to Last Build (Kurt, 2026-10-27: the Target's right-click menu; "if I click a costume in source
/// and it applies to target I should be able to get back to the original target"). Revert to Last Build: each target's package as its last build made it, and
/// the props as they were then (added props, their fits and bones), kept in the work folder's builds\last_builds.json (the
/// build's own output file stays in builds\, untouched by later changes, which go into new work copies). Reverting puts that
/// package back into the mod and the props back to that state (the editor applies them to it again, as after a build).
/// </summary>
sealed partial class ModelPage
{
    sealed record LastBuild(string Package, string Props, DateTime At);
    Dictionary<string, LastBuild>? lastBuilds;

    string LastBuildsFile => Path.Combine(host.WorkFolder, "builds", "last_builds.json");

    Dictionary<string, LastBuild> LastBuilds()
    {
        if (lastBuilds != null) return lastBuilds;
        try { lastBuilds = File.Exists(LastBuildsFile) ? JsonSerializer.Deserialize<Dictionary<string, LastBuild>>(File.ReadAllText(LastBuildsFile)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { lastBuilds = null; }
        return lastBuilds = new Dictionary<string, LastBuild>(lastBuilds ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A build of <paramref name="key"/> handed to the mod (its output file at <paramref name="path"/>): kept as its last build.</summary>
    void RememberBuild(string key, string path)
    {
        var all = LastBuilds();
        all[key] = new LastBuild(path, host.PropState(), DateTime.Now);
        try { Directory.CreateDirectory(Path.GetDirectoryName(LastBuildsFile)!); File.WriteAllText(LastBuildsFile, JsonSerializer.Serialize(all)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("last build: " + ex.Message); }
    }

    LastBuild? LastBuildOf(string key) => LastBuilds().TryGetValue(key, out var lb) && File.Exists(lb.Package) ? lb : null;

    /// <summary>The target's menu item: Revert to Last Build (disabled, with the reason, when it has none).</summary>
    ToolStripMenuItem RevertItem(CharacterList.Item t)
    {
        var lb = LastBuildOf(t.Key);
        return new ToolStripMenuItem("Revert to Last Build", null, (_, _) => _ = RevertToLastBuild(t.Key))
        {
            Enabled = lb != null,
            ToolTipText = lb != null
                ? $"Puts this package back as the build of {lb.At:MMM d, h:mm tt} made it, with the props as they were then (props added, fitted or moved since are put back as they were)."
                : "No build of this package in this mod yet (Build into Mod makes one).",
        };
    }

    /// <summary>The target's menu item: Revert to Original (the package before the Model tab built onto it).</summary>
    ToolStripMenuItem OriginalItem(CharacterList.Item t) => new("Revert to Original", null, (_, _) => _ = RevertToOriginal(t.Key))
    {
        ToolTipText = "Puts this costume back as it was before the Model tab built onto it (the mod's own copy from before, else the game's), takes off the props added to it here and clears the source, so the preview shows the costume as it is.",
    };

    /// <summary>
    /// The target back to its original: the package as it was before the Model tab built onto it (Model\base: the mod's own copy
    /// from before; else the game's stock copy), its added props and their bones taken off, and the source cleared so the
    /// preview shows the costume itself.
    /// </summary>
    internal async Task<bool> RevertToOriginal(string key, bool ask = true)
    {
        string kept = Path.Combine(host.WorkFolder, "base", key);
        string from;
        try { from = File.Exists(kept) ? kept : BasePackage.Resolve(key, true); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { Log($"{key}: its original isn't found ({ex.Message})."); return false; }
        bool modCopy = from == kept;
        if (ask && Dialog.Show(this, $"Put {key} back as it was before the Model tab built onto it ({(modCopy ? "the mod's own copy from before" : "the game's")})?\n\nThe model built onto it, the props added to it here and the source picked come off.",
                "Revert to Original", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
        // the props: this package's added props and bones dropped (the others kept)
        var state = System.Text.Json.Nodes.JsonNode.Parse(host.PropState()) as System.Text.Json.Nodes.JsonObject;
        if (state != null)
        {
            foreach (string list in new[] { "Added", "Bones" })
                if (state[list] is System.Text.Json.Nodes.JsonArray arr)
                    foreach (var n in arr.ToList())
                        if (n?["Package"]?.GetValue<string>() is string pk && pk.Equals(key, StringComparison.OrdinalIgnoreCase)) arr.Remove(n);
            if (!await host.SetPropState(state.ToJsonString()) || IsDisposed) { Log($"{key}: its added props couldn't be taken off."); return false; }
        }
        // a copy of the original in the work folder (the draft never points into the game folder)
        string copy = Path.Combine(UniqueDir(Path.Combine(host.WorkFolder, "builds", "original " + Path.GetFileNameWithoutExtension(key))), key);
        Protected.CheckWrite(copy);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(from, copy, true);
        // the source cleared: the preview shows the costume itself
        chosenKey = null; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear();
        if (!propMode) characters.ClearSelected();   // (in the Props view the pick on the left isn't the source: it stays)
        if (addedPick != null) { addedPick = null; MhoSkeleton.Choose(key, null); }
        host.SetPackage(key, copy);
        built.Remove(key); builtPrint.Remove(key);
        takenOff[key] = Fingerprint();
        propUndo.Clear(); propRedo.Clear();
        undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0;
        if (ChosenPackage?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true) await AddedPropsChanged(key, null);
        FillPackages();
        SaveState(); UpdateStatus(); SchedulePreview();
        Log($"{key}: back to its original ({(modCopy ? "the mod's own copy from before the Model tab" : "the game's")}); its added props are off and no source is picked.");
        return true;
    }

    internal async Task<bool> RevertToLastBuild(string key, bool ask = true)
    {
        if (LastBuildOf(key) is not { } lb) { Log($"{key}: no build to go back to."); return false; }
        if (ask && Dialog.Show(this, $"Put {key} back as its last build made it ({lb.At:MMM d, h:mm tt}), with the props as they were then?\n\nChanges made to it since (props added, fitted or moved, the bone they're held on) are dropped.",
                "Revert to Last Build", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return false;
        if (!await host.SetPropState(lb.Props) || IsDisposed) { Log($"{key}: the props couldn't be put back as they were at the last build."); return false; }
        host.SetPackage(key, lb.Package);   // (the editor puts the props of that state back in, as after a build)
        built[key] = lb.Package;
        takenOff.Remove(key);
        propUndo.Clear(); propRedo.Clear();
        undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0;
        if (ChosenPackage?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) == true) await AddedPropsChanged(key, null);
        SaveState(); SchedulePreview();
        Log($"{key}: back to its last build ({lb.At:MMM d, h:mm tt}, {lb.Package}), the props as they were then.");
        return true;
    }
}
