namespace MhoMffImporter.Gui;

sealed partial class ModelPage
{
    /// <summary>Test (--model-tab-test): the tab's own controls driven as a user would: the MFF character, one of the mod's
    /// packages, Build into Mod. The built file, or null when nothing was built into the draft.</summary>
    internal async Task<string?> TestBuild(string mff, string package, Action<string> say)
    {
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        characterFilter.Text = mff;
        await Task.Delay(500);
        Reselect(characters, mff);
        for (int i = 0; i < 1200 && !(chosenKey == mff && model != null); i++) await Task.Delay(100);
        if (model == null) { say("the MFF model didn't load: " + mff); return null; }
        say($"model {mff}: {parts.Rows.Count} parts");
        Reselect(packages, package);
        await Task.Delay(500);
        say("package: " + ChosenPackage?.Key + " from " + StartLabel(package));
        Build();
        for (int i = 0; i < 6000 && building; i++) await Task.Delay(100);
        say("log:\n  " + log.Text.Replace("\n", "\n  ").TrimEnd());
        return built.TryGetValue(package, out string? path) && File.Exists(path) ? path : null;
    }
}
