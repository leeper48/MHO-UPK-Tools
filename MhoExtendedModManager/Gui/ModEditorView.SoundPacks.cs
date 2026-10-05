using System.Text.RegularExpressions;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The Editor's Sound Packs tab.</summary>
sealed partial class ModEditorView
{
    // ---- Sound packs
    Control SoundsPage() => Page(sounds, Toolbar(Ui.FlatButton("Add .MHSFX Files", AddSounds, tip: "Add sound packs (.MHSFX): new voice lines and sounds."), Ui.Tip(Ui.FlatButton("Remove", () =>
        {
            foreach (DataGridViewRow r in sounds.SelectedRows) draft.SoundPacks.Remove((string)r.Tag!);
            RefreshSounds();
        }), "Take the selected sound packs out of the mod.")), "Sound packs (.MHSFX) add new voice or sound events to the game's sound files; the mod's packages play them by name.");

    void AddSounds()
    {
        using var d = new OpenFileDialog { Title = "Add Sound Packs", Filter = "Sound packs (*.mhsfx)|*.mhsfx", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (string f in d.FileNames)
        {
            try { SoundPack.Load(f); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { Dialog.Show(this, $"{Path.GetFileName(f)} isn't a readable sound pack: {ex.Message}", "Sound Packs"); continue; }
            if (!draft.SoundPacks.Contains(f, StringComparer.OrdinalIgnoreCase)) draft.SoundPacks.Add(f);
        }
        RefreshSounds();
    }

    void RefreshSounds()
    {
        sounds.Rows.Clear();
        foreach (string f in draft.SoundPacks)
        {
            string events = "?", pcks = "";
            try { var p = SoundPack.Load(f); events = p.Patches.Count.ToString(); pcks = string.Join(", ", p.Patches.Select(x => x.PckFile).Distinct()); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { pcks = "unreadable"; }
            sounds.Rows[sounds.Rows.Add(Path.GetFileName(f), events, pcks, f)].Tag = f;
        }
        sounds.ClearSelection();
    }

    /// <summary>Every page (a top tab's, or a sub-tab's under it) and how to show it: the test and snapshot hooks walk these.</summary>
    readonly List<(string Title, Action Show)> pages = [];

    /// <summary>A top-level tab: one page, or several under sub-tabs.</summary>
    void AddGroup(string title, params (string Title, Control Page)[] items)
    {
        int top = tabs.Count;
        if (items.Length == 1)
        {
            tabs.Add(title, items[0].Page);
            pages.Add((items[0].Title, () => tabs.Select(top)));
            return;
        }
        var inner = new FlatTabs { Dock = DockStyle.Fill, TabPoints = 9f };
        foreach (var (t, p) in items) inner.Add(t, p);
        tabs.Add(title, inner);
        for (int k = 0; k < items.Length; k++) { int kk = k; pages.Add((items[k].Title, () => { tabs.Select(top); inner.Select(kk); })); }
    }

    /// <summary>Test / snapshot hooks: the page count, showing a page, its title (sub-tabs included).</summary>
    public int TabCount => pages.Count;
    public void SelectTab(int i) { if (i >= 0 && i < pages.Count) pages[i].Show(); }

    /// <summary>A costume the icon tabs can filter to (Kurt): from UC__MarvelPlayer_&lt;Hero&gt;[_&lt;Costume&gt;]_SF.</summary>
    public sealed record CostumeFilter(string Label, List<string> Heroes, string Costume)
    {
        /// <summary>costume_storm_classic, costumestorm_classic, store_storm_classicblack, herohor_storm_classicblack …</summary>
        public bool Matches(string texture)
        {
            string t = texture.ToLowerInvariant();
            foreach (string prefix in new[] { "costume_", "costume", "store_", "herohor_", "teamup_" })
                if (t.StartsWith(prefix)) { t = t[prefix.Length..]; break; }
            int u = t.IndexOf('_');
            string hero = u < 0 ? t : t[..u], rest = u < 0 ? "" : t[(u + 1)..];
            if (!Heroes.Contains(hero)) return false;
            if (Costume.Length == 0) return true;
            string c = rest.Split('_')[0];
            return c.Length > 0 && (c.StartsWith(Costume) || Costume.StartsWith(c));
        }

        /// <summary>The filter for a UC__MarvelPlayer_&lt;Hero&gt;[_&lt;Costume&gt;]_SF package, or null for other packages.</summary>
        public static CostumeFilter? FromPackage(string file)
        {
            if (!file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) return null;
            var p = Path.GetFileNameWithoutExtension(file).Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) return null;
            // Only the final _SF is the seek-free suffix: uc__marvelplayer_blade_sf_SF is Blade's "sf" costume (herohor_blade_sf).
            var costume = p.Skip(3).ToList();
            if (costume.Count > 0 && costume[^1].Equals("SF", StringComparison.OrdinalIgnoreCase)) costume.RemoveAt(costume.Count - 1);
            string hero = AutoTags.DisplayName(p[2]) ?? p[2];
            string spaced = System.Text.RegularExpressions.Regex.Replace(string.Join(" ", costume), "(?<=[a-z0-9])(?=[A-Z])", " ");   // CivilWarMovie → Civil War Movie
            return new CostumeFilter(costume.Count > 0 ? $"{hero} {spaced}" : hero, AutoTags.Spellings(p[2]), string.Join("", costume).ToLowerInvariant());
        }
    }

    /// <summary>The selected costume package on the Packages tab, else the only one in the mod; null if none.</summary>
    CostumeFilter? SelectedCostume()
    {
        string? file = packages.SelectedRows.Count == 1 ? packages.SelectedRows[0].Tag as string : null;
        var costumes = draft.Packages.Select(p => p.File).Where(f => f.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (file == null || !file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) file = costumes.Count == 1 ? costumes[0] : null;
        return file == null ? null : CostumeFilter.FromPackage(file);
    }
    public string TabTitle(int i) => i >= 0 && i < pages.Count ? pages[i].Title : "";

    /// <summary>Test hook (--editor-save-test): visits every tab so each page loads, then saves as the Save button does.</summary>
    public async Task<string?> SaveForTest()
    {
        for (int i = 0; i < pages.Count; i++) { pages[i].Show(); await Task.Delay(pages[i].Title is "Packages" or "Icons" or "Store Images" ? 5000 : 500); }
        Save();
        return SavedName;
    }

    /// <summary>Test hook: the Strings tab's state (replacement column editable, rows with a Used By text).</summary>
    public string StringsCheck() => stringsPage.Check();

    /// <summary>Test hook: search the Strings tab and wait for the results and the Used By column.</summary>
    public async Task SearchStringsForTest(string text)
    {
        SelectTab(pages.FindIndex(p => p.Title == "Strings"));
        await Task.Delay(4000);
        stringsPage.SearchForTest(text);
        await Task.Delay(3000);
    }
}
