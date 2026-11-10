namespace MhoExtendedModManager.Gui;

/// <summary>
/// Powers tab → a character's Own Effects entry → Borrow Effects (a user, 2026-11-09; Kurt chose the Powers tab, sources from
/// heroes, team-ups and NPCs): another character's always-on effects on this one (OwnFxBorrow). Every costume of the mod gets
/// an Own Effects entry for it, also one with none of its own. The build goes into the draft's package at once (the preview
/// shows it), and into the copies other features rebuild that package from (borrowed looks, recolors, dodge ghosts), since
/// the attachment list is worked out from the package's original list each time; Save builds it again on the package as it
/// is then (after the ghosts and props, before the colors, so a recolor reaches the borrowed effects). Reset puts the
/// original list back.
/// </summary>
sealed partial class ModEditorView
{
    /// <summary>Packages built for borrowed effects since the last save (the preview reads them).</summary>
    readonly HashSet<string> ownFxTouched = new(StringComparer.OrdinalIgnoreCase);
    bool ownFxBuilding;

    OwnFxBorrowEntry? OwnFxOf(string file) => draft.OwnFxBorrows.FirstOrDefault(e => e.Package.Equals(file, StringComparison.OrdinalIgnoreCase));

    /// <summary>A costume's hero base package (the game's clean copy), or null (a hero's base itself, a team-up, an NPC).</summary>
    string? OwnFxBase(string file)
    {
        if (game == null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(file, @"^UC__MarvelPlayer_([A-Za-z0-9]+)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string bf = $"UC__MarvelPlayer_{m.Groups[1].Value}_SF.upk";
        if (bf.Equals(file, StringComparison.OrdinalIgnoreCase)) return null;
        string? p = DraftPath(bf) is string d && File.Exists(d) ? d : StockFiles.For(game.Cooked, bf);
        return p != null && File.Exists(p) ? p : null;
    }

    /// <summary>The character packages of the mod whose Own Effects entry can take borrowed effects: heroes and costumes,
    /// team-ups, NPCs and enemies.</summary>
    static bool CanBorrowOwnFx(string file) =>
        file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase)
        || file.StartsWith("UC__MarvelAgent_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("UC__MarvelNPC_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Borrow / Reset buttons and the caption for an Own Effects entry (ShowBorrow's other parts stay hidden: no
    /// power borrow on such an entry).</summary>
    void ShowOwnFx(Fx.PowerList.Power p)
    {
        string f = p.Prototype[PowerColorBuild.OwnPrefix.Length..];
        var e = OwnFxOf(f);
        bool can = CanBorrowOwnFx(f) && game != null;
        if (borrowButton != null) { borrowButton.Enabled = can && !ownFxBuilding; Ui.TipTitled(borrowButton, "Borrow Effects", "Gives this character another character's always-on effects (Ghost Rider's burning head, Firestar's flames, Cable's eye glow): heroes, team-ups, NPCs and enemies. On top of its own, or instead of them. Only this package changes."); }
        if (borrowRemove != null) { borrowRemove.Enabled = e != null && !ownFxBuilding; Ui.TipTitled(borrowRemove, "Reset Effects", "Takes the borrowed effects off again: this character's own (if any) as the game has them."); }
        if (borrowHead != null) borrowHead.Text = "BORROW EFFECTS";
        borrowCaption.Text = ownFxBuilding ? "Building the borrowed effects…"
            : e != null ? $"Has {e.Name}'s effects ({(e.Replace ? "instead of its own" : "on top of its own")})"
            : can ? "Its own effects (Borrow Effects adds another character's)" : "This package can't take other effects";
    }

    void PickOwnFx(Fx.PowerList.Power p)
    {
        if (game == null) return;
        string f = p.Prototype[PowerColorBuild.OwnPrefix.Length..];
        if (!CanBorrowOwnFx(f)) return;
        if (!draft.Packages.Any(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase)) && !AddPackageFromGame(f)) return;
        var current = OwnFxOf(f);
        string path = DraftPath(f)!;
        // its own effect attachments (as the game has them: a borrow's are left out)
        var own = OwnFxBorrow.Effects(path, OwnFxBase(f)).Select(x => x.Class).Where(c => current == null || !c.Equals(current.Class, StringComparison.OrdinalIgnoreCase)).ToList();
        using var form = new OwnFxForm(p.Name.Replace(": Own Effects", ""), own, current);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result is not { } chosen) return;
        chosen.Package = f;
        // the list before any borrow: kept from the first one (a reset puts it back, so a new borrow reads it again)
        chosen.Original = current != null ? current.Original : ModMeshes.AttachmentClasses(path);
        _ = BuildOwnFx(f, chosen, current);
    }

    void ResetOwnFx(Fx.PowerList.Power p)
    {
        string f = p.Prototype[PowerColorBuild.OwnPrefix.Length..];
        if (OwnFxOf(f) is not { } e) return;
        _ = BuildOwnFx(f, null, e);
    }

    /// <summary>Builds <paramref name="entry"/> (null: back to <paramref name="before"/>'s original list) into the draft's
    /// package and the copies other features rebuild it from, then shows it.</summary>
    async Task BuildOwnFx(string f, OwnFxBorrowEntry? entry, OwnFxBorrowEntry? before)
    {
        if (game == null) return;
        ownFxBuilding = true;
        if (powerList.SelectedItem is Fx.PowerList.Power s0) ShowBorrow(s0);
        var log = new List<string>();
        string? built = null, error = null;
        var basis = entry ?? before!;
        string? baseFile = OwnFxBase(f);
        string cooked = game.Cooked;
        string? now = DraftPath(f);
        var kept = KeptCopies(f);
        try
        {
            built = await Task.Run(() =>
            {
                string? donor = entry != null ? StockFiles.For(cooked, entry.Donor) : null;
                if (entry != null && (donor == null || !File.Exists(donor))) throw new InvalidDataException($"{entry.Donor} isn't in the game folder");
                byte[] Make(string path) => OwnFxBorrow.Rebuild(File.ReadAllBytes(path), f, baseFile, basis.Original, donor, entry?.Class, entry?.Replace == true, log);
                if (now == null || !File.Exists(now)) throw new InvalidDataException($"{f} isn't in the mod");
                string dir = Path.Combine(WorkFolder, "ownfx_build", Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, f);
                File.WriteAllBytes(file, Make(now));
                foreach (string k in kept) File.WriteAllBytes(k, Make(k));   // (in place: the work copies other features start from)
                return file;
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException or ArgumentException or IndexOutOfRangeException) { error = ex.Message; }
        ownFxBuilding = false;
        if (IsDisposed) return;
        if (built != null)
        {
            draft.OwnFxBorrows.RemoveAll(x => x.Package.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (entry != null) draft.OwnFxBorrows.Add(entry);
            int k = draft.Packages.FindIndex(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) draft.Packages[k] = (f, built);
            ownFxTouched.Add(f);
            RefreshPackages();
            if (powerPreview != null) { powerPreview.OverrideFiles = PreviewOverrides; powerPreview.ReloadOverrides(); }
            FillPowers();
        }
        else Dialog.Show(this, "The effects couldn't be borrowed: " + error, "Not Borrowed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (powerList.SelectedItem is Fx.PowerList.Power s) { ShowBorrow(s); if (built != null) PowerSelected(); }
    }

    /// <summary>The copies of <paramref name="f"/> that other features rebuild it from (they take the borrowed effects too):
    /// the borrowed looks' start and preview build, the copy before a recolor, the copy before dodge ghosts and their preview build.</summary>
    List<string> KeptCopies(string f)
    {
        var list = new List<string>();
        foreach (string sub in new[] { "borrow_base", "color_base", "ghost_base" })
            if (Path.Combine(WorkFolder, sub, f) is var k && File.Exists(k)) list.Add(k);
        if (borrowFiles.TryGetValue(f, out var bf) && File.Exists(bf)) list.Add(bf);
        if (ghostBuilt.TryGetValue(f, out var gb) && File.Exists(gb.Path)) list.Add(gb.Path);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Save (after the ghosts and props, before the colors): each package with borrowed effects gets them on the package as
    /// it is now (borrowed looks and ghosts may have rebuilt it since); a package whose borrow was reset gets its original list
    /// back (it's that already when the reset was built; again here in case another feature rebuilt it from an older copy).
    /// </summary>
    void ApplyOwnFx(List<string> log)
    {
        if (game == null) return;
        var before = editing?.Manifest.OwnFxBorrows ?? [];
        foreach (var e in draft.OwnFxBorrows.Concat(before.Where(b => OwnFxOf(b.Package) == null)).ToList())
        {
            bool reset = OwnFxOf(e.Package) == null;
            string? now = DraftPath(e.Package);
            if (now == null || !File.Exists(now)) continue;
            string? donor = reset ? null : StockFiles.For(game.Cooked, e.Donor);
            if (!reset && (donor == null || !File.Exists(donor))) throw new InvalidDataException($"{e.Donor} (the borrowed effects' character) isn't in the game folder");
            byte[] bytes = OwnFxBorrow.Rebuild(File.ReadAllBytes(now), e.Package, OwnFxBase(e.Package), e.Original, donor, reset ? null : e.Class, !reset && e.Replace, log);
            string dir = Path.Combine(WorkFolder, "ownfx_build", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, e.Package);
            File.WriteAllBytes(file, bytes);
            int k = draft.Packages.FindIndex(x => x.File.Equals(e.Package, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) draft.Packages[k] = (e.Package, file);
            string colorBase = Path.Combine(WorkFolder, "color_base", e.Package);
            if (File.Exists(colorBase)) File.WriteAllBytes(colorBase, OwnFxBorrow.Rebuild(File.ReadAllBytes(colorBase), e.Package, OwnFxBase(e.Package), e.Original, donor, reset ? null : e.Class, !reset && e.Replace, []));
            log.Add(reset ? $"{e.Package}: its own effects again" : $"{e.Package}: {e.Name}'s effects{(e.Replace ? " instead of its own" : "")}");
        }
    }
}

sealed partial class ModEditorView
{
    /// <summary>--own-fx-tab-test: the Own Effects entry of <paramref name="file"/> picked, <paramref name="donorClass"/> of
    /// <paramref name="donorFile"/> borrowed (no window), the draft's package checked.</summary>
    internal async Task<List<string>> OwnFxTestAsync(string file, string donorFile, string donorClass, bool replace)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add($"{(ok ? "PASS" : "FAIL")} {what}");
        SelectTab(Enumerable.Range(0, TabCount).First(i => TabTitle(i) == "Powers"));
        string proto = PowerColorBuild.OwnPrefix + file;
        for (int i = 0; i < 600 && !powerList.Items.Cast<Fx.PowerList.Power>().Any(x => x.Prototype.Equals(proto, StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(100);
        await Task.Delay(4000);
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Prototype.Equals(proto, StringComparison.OrdinalIgnoreCase));
        Check($"an Own Effects entry for {file}", at >= 0);
        if (at < 0) return lines;
        powerList.SelectedIndex = at;
        await Task.Delay(500);
        Check($"Borrow Effects ready: \"{borrowHead?.Text}\", {borrowCaption.Text}", borrowHead?.Text == "BORROW EFFECTS" && borrowButton?.Enabled == true);
        string path = DraftPath(file)!;
        var entry = new OwnFxBorrowEntry { Package = file, Donor = donorFile, Class = donorClass, Name = donorFile, Replace = replace, Original = ModMeshes.AttachmentClasses(path) };
        await BuildOwnFx(file, entry, null);
        var list = ModMeshes.AttachmentClasses(DraftPath(file)!) ?? [];
        Check($"the draft's package lists it: {string.Join(", ", list)}", list.Contains(donorClass, StringComparer.OrdinalIgnoreCase));
        Check($"the preview reads the build ({PreviewOverrides().ContainsKey(file)})", PreviewOverrides().ContainsKey(file));
        Check($"caption: {borrowCaption.Text}", borrowCaption.Text.StartsWith("Has "));
        return lines;
    }

    internal async Task<bool> ResetOwnFxForTest(string file)
    {
        SelectTab(Enumerable.Range(0, TabCount).First(i => TabTitle(i) == "Powers"));
        string proto = PowerColorBuild.OwnPrefix + file;
        for (int i = 0; i < 600 && !powerList.Items.Cast<Fx.PowerList.Power>().Any(x => x.Prototype.Equals(proto, StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(100);
        await Task.Delay(3000);
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => x.Prototype.Equals(proto, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || OwnFxOf(file) is not { } e) return false;
        powerList.SelectedIndex = at;
        await BuildOwnFx(file, null, e);
        return OwnFxOf(file) == null;
    }
}
