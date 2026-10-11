namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Powers tab's Borrowed Look (Kurt, 2026-10-10: a hero borrowing another hero's power look, client side only): the
/// selected power takes another hero's power's look (PowerBorrow: the whole power, or its timing kept with only what its
/// effects draw). Picking or removing one rebuilds every borrow into a work folder at once, so the 3D preview shows it (its
/// OverrideFiles); Save rebuilds them again, before the power colors, which then recolor the borrowed look.
/// </summary>
sealed partial class ModEditorView
{
    readonly Label borrowCaption = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 2, 0, 4), MaximumSize = new Size(560, 0) };
    Button? borrowButton, borrowRemove;
    /// <summary>The borrowed packages built for the preview (file → path), and their folder.</summary>
    Dictionary<string, string> borrowFiles = new(StringComparer.OrdinalIgnoreCase);
    string? borrowWork;
    bool borrowBuilding;

    Control BorrowSection()
    {
        var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 0) };
        box.Controls.Add(new Label { Text = "BORROWED LOOK", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 2) });
        borrowButton = Ui.FlatButton("Borrow Look", PickBorrow, tip: "Give this power another hero's power look: its animation, effects and projectile, or only what its effects draw while this power keeps its timing. Only the look changes (what the power does stays this one's), on this hero's power packages: every costume of the hero, anyone using this mod; the other hero stays as it is.");
        borrowRemove = Ui.FlatButton("Game's Look", RemoveBorrow, tip: "Take this power back to its own look (the borrowed one comes out of the mod at Save).");
        box.Controls.Add(Toolbar(borrowButton, borrowRemove));
        box.Controls.Add(borrowCaption);
        return box;
    }

    PowerBorrowEntry? BorrowOf(string proto) => draft.PowerBorrows.FirstOrDefault(e => e.Power.Equals(proto, StringComparison.OrdinalIgnoreCase));

    /// <summary>The selected power's borrow, in the caption and the buttons (a hero's power with an animation only).</summary>
    void ShowBorrow(Fx.PowerList.Power p)
    {
        bool can = !PowerColorBuild.IsOwn(p.Prototype) && !agentOwner.ContainsKey(p.Prototype) && p.Animations.Count > 0 && PowerBorrow.HeroOf(p.Prototype) != null;
        var e = BorrowOf(p.Prototype);
        if (borrowButton != null) borrowButton.Enabled = can && !borrowBuilding;
        if (borrowRemove != null) borrowRemove.Enabled = e != null && !borrowBuilding;
        borrowCaption.Text = borrowBuilding ? "Building the borrowed looks…"
            : e != null ? $"Looks like {e.DonorName} ({(e.KeepTiming ? "its effects, this power's timing" : e.Fit ? "the whole power, fitted to this power's timing" : "the whole power, at its own speed")})"
            : can ? "Its own look" : "Only a hero's powers with an animation can borrow a look";
    }

    void PickBorrow()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p || game == null) return;
        var g = game;
        powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
        using var f = new BorrowPowerForm(powerDb, g.Cooked, p.Prototype, p.Name, BorrowOf(p.Prototype));
        if (f.ShowDialog(this) != DialogResult.OK || f.Result is not { } chosen) return;
        if (chosen.Donor.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase)) { borrowCaption.Text = "That's this power's own look"; return; }
        draft.PowerBorrows.RemoveAll(e => e.Power.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase));
        chosen.Power = p.Prototype; chosen.Name = p.Name;
        draft.PowerBorrows.Add(chosen);
        RebuildBorrows();
    }

    void RemoveBorrow()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p) return;
        draft.PowerBorrows.RemoveAll(e => e.Power.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase));
        RebuildBorrows();
    }

    /// <summary>Every borrow built again (in the background), then the preview reads the built packages.</summary>
    void RebuildBorrows()
    {
        if (game == null || powerDb == null && draft.PowerBorrows.Count > 0 && game == null) return;
        var g = game;
        borrowBuilding = true;
        if (powerList.SelectedItem is Fx.PowerList.Power sel) ShowBorrow(sel);
        string work = Path.Combine(lib.DataFolder, "power-borrow-" + Guid.NewGuid().ToString("N")[..8]);
        string model = WorkFolder;
        var log = new List<string>();
        Task.Run(() =>
        {
            powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
            return PowerBorrow.Rebuild(draft, editing, lib, g, powerDb, work, model, log);
        }).ContinueWith(t =>
        {
            borrowBuilding = false;
            if (IsDisposed) return;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                string why = t.Exception?.InnerException?.Message ?? "unknown";
                Dialog.Show(this, "The borrowed look couldn't be made: " + why, "Look Not Borrowed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (powerList.SelectedItem is Fx.PowerList.Power s0) ShowBorrow(s0);
                return;
            }
            string? old = borrowWork;
            borrowWork = work;
            borrowFiles = t.Result;
            if (old != null) try { Directory.Delete(old, true); } catch (IOException) { }
            RefreshPackages();
            paletteCache.Clear();   // a borrowed power's colors are the borrowed look's
            powerList.Invalidate();
            if (powerPreview != null) { powerPreview.OverrideFiles = () => borrowFiles; powerPreview.ReloadOverrides(); }
            if (powerList.SelectedItem is Fx.PowerList.Power s) ShowBorrow(s);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Before saving: the borrows built again into the draft (before the power colors). The packages they built
    /// (for the colors to start from), and the work folder.</summary>
    (HashSet<string> Built, string? Work) ApplyPowerBorrows(List<string> log)
    {
        bool had = editing?.Manifest.PowerBorrows is { Count: > 0 };
        if (draft.PowerBorrows.Count == 0 && !had) return ([], null);
        if (game == null) throw new InvalidDataException("the game folder isn't set");
        powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(game.Root, "Data", "Game", "Calligraphy.sip")));
        string work = Path.Combine(lib.DataFolder, "power-borrow-" + Guid.NewGuid().ToString("N")[..8]);
        var built = PowerBorrow.Rebuild(draft, editing, lib, game, powerDb, work, WorkFolder, log);
        return (built.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), work);
    }
}
