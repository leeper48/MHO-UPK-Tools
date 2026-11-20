using MhoExtendedModManager.Model.Gui;
namespace MhoExtendedModManager.Gui;

/// <summary>
/// A power's right-click menu (Kurt, 2026-11-17), on the Powers tab's tiles and the Mods and Model tabs' power buttons: Rename
/// Power, Replace Icon, Make Icon From 3D View and Change Its Animations. The name and icon are changed where the menu was
/// opened (they're the draft's strings and icons); the animations on the Animations tab, opened on that power's animations.
/// </summary>
sealed partial class ModEditorView
{
    PowerLooks IModelHost.PowerLooks => Looks;

    void IModelHost.PowerAction(string prototype, PowerMenuAction action, Preview3D? view)
    {
        // (the Powers tab's own actions: there, on that power)
        if (OnPowersTabOnly(action)) PowerAction(prototype, action);
        else DoPowerAction(prototype, action, view, onPowersTab: false);
    }

    static bool OnPowersTabOnly(PowerMenuAction a) => a is PowerMenuAction.Borrow or PowerMenuAction.ResetBorrow or PowerMenuAction.Color or PowerMenuAction.Dark or PowerMenuAction.GamesColors or PowerMenuAction.Parts;

    /// <summary>The Mods tab's power buttons: the Powers tab opens on the power, then the action is done there.</summary>
    public async void PowerAction(string prototype, PowerMenuAction action)
    {
        if (action == PowerMenuAction.ModifyAnimation) { DoPowerAction(prototype, action, null, onPowersTab: false); return; }
        ShowPage("Powers");
        // (the tab fills its powers in the background the first time it shows)
        for (int i = 0; i < 300 && !IsDisposed && !powerList.Items.OfType<Fx.PowerList.Power>().Any(p => p.Part == null && Same(p.Prototype, prototype)); i++) await Task.Delay(100);
        if (IsDisposed) return;
        int at = powerList.Items.FindIndex(o => o is Fx.PowerList.Power p && p.Part == null && Same(p.Prototype, prototype));
        if (at >= 0 && powerList.SelectedIndex != at) powerList.SelectedIndex = at;
        if (action == PowerMenuAction.MakeIcon) await Task.Delay(600);   // (the power's animation loads first)
        if (at < 0 && OnPowersTabOnly(action)) { Dialog.Show(this, "That power isn't in the Powers tab's list.", "Not Found", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        DoPowerAction(prototype, action, powerPreview?.View3D, onPowersTab: true);
    }

    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Powers tab's tiles: the same menu (a hero's own powers only).</summary>
    void PowerTileMenu(int index, Point screen)
    {
        if (index < 0 || index >= powerList.Items.Count || powerList.Items[index] is not Fx.PowerList.Power p || p.Part != null) return;
        var menu = new ContextMenuStrip();
        if (index >= heroPowerCount)
        {
            // the character's own entries (a dodge, a landing, a hover …; Kurt, 2026-11-20: options there too): their animations
            if (p.Animations.Count == 0) { menu.Dispose(); return; }
            menu.Items.Add(new ToolStripMenuItem("Change Its Animations", null, (_, _) => ShowPowerAnimations(p)) { ToolTipText = "Open the Animations tab on its animations, to swap them for another character's." });
        }
        else PowerMenuItems.Fill(menu, p, BorrowOf(p.Prototype) != null, a => DoPowerAction(p.Prototype, a, powerPreview?.View3D, onPowersTab: true));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Ui.ShowAt(menu, screen);
    }

    /// <summary>A hero power by prototype from whichever list has it (the Powers tab's, the previews'); null if none does.</summary>
    Fx.PowerList.Power? PowerByProto(string proto) =>
        powerList.Items.OfType<Fx.PowerList.Power>().FirstOrDefault(p => p.Part == null && Same(p.Prototype, proto))
        ?? new[] { powerPreview?.AllHeroPowers, animPreview?.AllHeroPowers, modelPage?.HeroPowers }.SelectMany(l => l ?? []).FirstOrDefault(p => Same(p.Prototype, proto));

    Fx.GameData? PowerDbNow()
    {
        if (powerDb == null && game != null) powerDb = PowerPreview.GameDb(game.Cooked).Result;
        return powerDb;
    }

    void DoPowerAction(string proto, PowerMenuAction action, Preview3D? view, bool onPowersTab)
    {
        if (game == null) return;
        // one animation (an idle / walk button, ▶): its Animations tab row
        if (proto.StartsWith(Preview3D.AnimPrefix, StringComparison.Ordinal)) { if (action == PowerMenuAction.ModifyAnimation) ShowAnimation(proto[Preview3D.AnimPrefix.Length..]); return; }
        var p = PowerByProto(proto) ?? new Fx.PowerList.Power(proto, Path.GetFileNameWithoutExtension(proto), null, []);
        switch (action)
        {
            case PowerMenuAction.Rename: RenamePower(p); break;
            case PowerMenuAction.ReplaceIcon:
                if (PickIconImage() is string image && PutCustomIcon(proto, BorrowOf(proto), image)) PowerLookChanged(proto, view);
                break;
            case PowerMenuAction.MakeIcon: MakePowerIcon(p, view, onPowersTab); break;
            case PowerMenuAction.ModifyAnimation: ShowPowerAnimations(p); break;
            // the Powers tab's own actions act on the selected power (PowerAction selected it)
            case PowerMenuAction.Borrow: PickBorrow(); break;
            case PowerMenuAction.ResetBorrow: RemoveBorrow(); break;
            case PowerMenuAction.Color: powerSideTabs?.Select(0); break;
            case PowerMenuAction.Dark: powerSideTabs?.Select(0); SetDark(); break;
            case PowerMenuAction.GamesColors: powerSideTabs?.Select(0); SetColor(null); break;
            case PowerMenuAction.Parts: powerSideTabs?.Select(1); break;
        }
    }

    /// <summary>
    /// Rename Power: the power's DisplayName string (English) in the draft. A power with a borrowed look keeps the name on its
    /// borrow (Reset Power takes it off); the game's own name (or an empty box) takes the change off.
    /// </summary>
    void RenamePower(Fx.PowerList.Power p)
    {
        if (PowerDbNow() is not { } db || db.Find(p.Prototype) is not { } pe || Fx.PowerList.Field(db, pe.Id, "DisplayName", 'S', true) is not { Found: true, Raw: var id and not 0 })
        {
            Dialog.Show(this, "This power has no name of its own in the game data, so it can't be renamed.", "Not Renamed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string now = Looks.Name(p, db), original = Looks.Original(p, db);
        if (Ui.Prompt(this, "Rename Power", $"The power's name in game (English; the game's own: {original}):", now) is not string typed) return;
        string name = typed.Trim();
        if (name == now) return;
        var e = BorrowOf(p.Prototype);
        if (name.Length == 0 || name == original)
        {
            if (e != null) TakeOffName(e);
            draft.Strings.RemoveAll(x => x.Language == "eng" && x.Id == id);
            stringsPage.Drop("eng", id);
        }
        else if (e != null) PutNameAndIcon(e, name, false);
        else
        {
            var s = new StringReplacement("eng", CostumeMove.StringFileFor("eng", id), id, name);
            draft.Strings.RemoveAll(x => x.Language == "eng" && x.Id == id);
            draft.Strings.Add(s);
            stringsPage.Put(s);
        }
        PowerLookChanged(p.Prototype, null);
        modelPage?.RefreshPowerLooks();
    }

    /// <summary>Everything showing a power's name or icon, again.</summary>
    void PowerLookChanged(string proto, Preview3D? view)
    {
        powerList.Invalidate();
        powerIcon.Invalidate();
        ShowPowerTitle();
        if (powerList.SelectedItem is Fx.PowerList.Power sel && Same(sel.Prototype, proto)) { ShowNameIcon(BorrowOf(proto)); ShowCustomIcon(sel); }
        view?.RefreshLooks();
    }

    /// <summary>Make Icon From 3D View: the power plays in the view, the icon maker snapshots it.</summary>
    void MakePowerIcon(Fx.PowerList.Power p, Preview3D? view, bool onPowersTab)
    {
        if (view == null) { Dialog.Show(this, "The 3D view isn't ready yet: try again once the character shows.", "No 3D View", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (PowerIconTextures(p.Prototype).Count == 0 && PowerDbNow() != null)
        {
            Dialog.Show(this, "This power has no icon in the game to replace.", "Icon Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // (the Powers tab plays the selected power already; elsewhere the button's power is picked, paused)
        if (!onPowersTab) view.PlayPower(p.Prototype);
        var f = new PowerIconForm(view, Looks.Name(p, PowerDbNow()));
        f.Use += png => { if (PutCustomIcon(p.Prototype, BorrowOf(p.Prototype), png)) { PowerLookChanged(p.Prototype, view); modelPage?.RefreshPowerLooks(); } };
        f.FormClosed += (_, _) => f.Dispose();
        f.Show(FindForm());
        f.PlaceBeside(view);
    }

    /// <summary>Change Its Animations: the Animations tab, found to the power's animations, its first one selected.</summary>
    async void ShowPowerAnimations(Fx.PowerList.Power p)
    {
        ShowPage("Animations");
        for (int i = 0; i < 300 && !IsDisposed && (animGrid == null || animPreview == null || !animPreview.HeroPowersReady); i++) await Task.Delay(100);
        if (IsDisposed || animGrid == null) return;
        // the grid's Power column has the preview's name for it (the game folder's strings)
        string column = animPreview?.AllHeroPowers.FirstOrDefault(x => Same(x.Prototype, p.Prototype))?.Name ?? p.Name;
        animFind.Text = column;
        var row = animGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is CostumeAnims.Anim a && p.Animations.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
            ?? animGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (row != null) { animGrid.CurrentCell = row.Cells[0]; animGrid.ClearSelection(); row.Selected = true; animGrid.FirstDisplayedScrollingRowIndex = row.Index; }
        animGrid.Focus();
    }

    /// <summary>The Animations tab on one animation (found by its name, selected).</summary>
    async void ShowAnimation(string name)
    {
        ShowPage("Animations");
        for (int i = 0; i < 300 && !IsDisposed && (animGrid == null || animGrid.Rows.Count == 0); i++) await Task.Delay(100);
        if (IsDisposed || animGrid == null) return;
        animFind.Text = name;
        var row = animGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is CostumeAnims.Anim a && Same(a.Name, name))
            ?? animGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (row != null) { animGrid.CurrentCell = row.Cells[0]; animGrid.ClearSelection(); row.Selected = true; animGrid.FirstDisplayedScrollingRowIndex = row.Index; }
        animGrid.Focus();
    }

    /// <summary>A page by its title (sub-tabs included).</summary>
    void ShowPage(string title) { foreach (var (t, show) in pages) if (t == title) { show(); return; } }
}
