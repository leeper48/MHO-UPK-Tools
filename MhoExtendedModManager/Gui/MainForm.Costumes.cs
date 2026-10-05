using System.Diagnostics;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The main window's Move to Another Costume (and to another hero).</summary>
sealed partial class MainForm
{
    // ---- Move to Another Costume

    List<Costume>? costumes;
    string? costumesRoot;
    readonly Dictionary<string, (string File, Costume Costume)?> singleCostumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one costume a mod is for (null until the game's costume data is read, or for other mods).</summary>
    (string File, Costume Costume)? SingleCostume(Mod m)
    {
        if (costumes == null) return null;
        if (!singleCostumes.TryGetValue(m.FolderName, out var c)) singleCostumes[m.FolderName] = c = CostumeMove.Single(m, costumes, allowBase: true);
        return c;
    }

    /// <summary>The header's costume drop-down: the hero's other costumes.</summary>
    void CostumeMenu(Mod m, Point pt)
    {
        var menu = NewMenu();
        FillCostumeItems(menu.Items, m);
        Ui.ShowAt(menu, pt);
    }

    /// <summary>The hero's other costumes with their store images; one already made from this mod selects that mod.</summary>
    void FillCostumeItems(ToolStripItemCollection items, Mod m)
    {
        if (SingleCostume(m) is not { } src || lib == null || game == null || costumes == null) return;
        var catalog = list.Catalog;
        items.Add(new ToolStripMenuItem($"\"{m.Name}\" Is for {src.Costume.Title}. Move It To:") { Enabled = false });
        // A costume in the hero's main package (Star-Lord Infinity War) can't be renamed onto another costume: its model is
        // copied into the target's own package instead (as for another hero).
        bool copy = CostumeMove.IsBase(src.Costume);
        foreach (var target in CostumeMove.Targets(src.Costume, costumes, game.Cooked))
        {
            var made = lib.Mods.FirstOrDefault(x => x.Name.Equals(copy ? CrossMove.NewName(m, target) : CostumeMove.NewName(m, target), StringComparison.OrdinalIgnoreCase));
            var others = lib.Mods.Where(x => x != m && x != made && x.Manifest.UpkReplacements.Contains(target.Package, StringComparer.OrdinalIgnoreCase)).ToList();
            string text = target.Title + (target.IsDefault ? "  ·  Default" : "") + (made != null ? "  ·  Already Made (Select It)" : others.Count > 0 ? $"  ·  {others.Count} Other Mod(s)" : "");
            var item = new ToolStripMenuItem(text, null, (_, _) => { if (made != null) SelectMod(made.FolderName); else if (copy) CopyMove(m, src.File, src.Costume, target); else MoveCostume(m, src.File, src.Costume, target); });
            if (target.IsDefault) item.Tag = Ui.Enabled;
            if (MoveCostumeForm.Image(target, catalog) is Bitmap b) { item.Image = b; item.ImageScaling = ToolStripItemImageScaling.None; item.Image = new Bitmap(b, new Size((int)(24 * DeviceDpi / 96f), (int)(34 * DeviceDpi / 96f))); b.Dispose(); }
            items.Add(item);
        }
        // Another hero (Kurt): a picker of heroes, then their costumes.
        items.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem("Another Hero", null, (_, _) => PickOtherHero(m, src.File, src.Costume));
        other.ToolTipText = "Move the model and voice onto a costume of a different hero (that hero's animations and powers stay).";
        items.Add(other);
        // The hero's default costume when it lives in the hero's main package (Thor Modern): shown, but it can't be a target.
        var def = costumes.FirstOrDefault(c => c.IsDefault && c.Hero == src.Costume.Hero && CostumeMove.IsBase(c));
        if (def != null && !def.Class.Equals(src.Costume.Class, StringComparison.OrdinalIgnoreCase))
            items.Add(new ToolStripMenuItem($"{def.Title}  ·  Default (Can't Move Here: Its Package Holds the Hero's Animations)") { Enabled = false, Tag = Ui.Enabled });
    }

    /// <summary>Another Hero: the hero / costume picker, then the Move window and CrossMove.CreateMod.</summary>
    async void PickOtherHero(Mod m, string file, Costume source)
    {
        if (readOnly || lib == null || game == null || costumes == null) return;
        var (l, g, all) = (lib, game, costumes);
        var catalog = list.Catalog;
        Costume target;
        using (var pick = new HeroPickerForm(m, source, all, g.Cooked, catalog))
        {
            if (pick.ShowDialog(this) != DialogResult.OK || pick.Chosen == null) return;
            target = pick.Chosen;
        }
        CopyMove(m, file, source, target);
    }

    /// <summary>The copy route (CrossMove): the target costume's stock package with the mod's model copied in. For another hero,
    /// and for a costume in the hero's main package (Star-Lord Infinity War) moving to one of the hero's own costumes.</summary>
    async void CopyMove(Mod m, string file, Costume source, Costume target)
    {
        if (readOnly || lib == null || game == null || costumes == null) return;
        var (l, g, all) = (lib, game, costumes);
        var catalog = list.Catalog;
        var made0 = l.Mods.FirstOrDefault(x => x.Name.Equals(CrossMove.NewName(m, target), StringComparison.OrdinalIgnoreCase));
        if (made0 != null) { SelectMod(made0.FolderName); status.Text = Ui.TitleCase($"\"{made0.Name}\" is made already"); return; }
        UseWaitCursor = true;
        CostumeMove.Plan plan;
        try { plan = await Task.Run(() => CostumeMove.Make(m, file, source, target, all, g.Cooked, catalog)); }
        finally { UseWaitCursor = false; }
        var onTarget = l.Mods.Where(x => x.Manifest.UpkReplacements.Contains(target.Package, StringComparer.OrdinalIgnoreCase)).ToList();
        bool swap;
        using (var f = new MoveCostumeForm(m, plan, catalog, onTarget, crossHero: true))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            swap = f.SwapOn;
        }
        UseWaitCursor = true;
        status.Text = Ui.TitleCase($"Moving \"{m.Name}\" to {target.Title}…");
        string? error = null, made;
        var log = new List<string>();
        try { made = await Task.Run(() => CrossMove.CreateMod(l, m, file, source, target, all, g, catalog, log, out error)); }
        finally { UseWaitCursor = false; }
        if (made == null) { Dialog.Show(this, error ?? "Unknown error.", "Not Moved", MessageBoxButtons.OK, MessageBoxIcon.Error); Reload(); return; }
        if (log.Where(x => x.StartsWith("resized ")).Select(x => x[8..]).ToList() is { Count: > 0 } resizedImages)
            Dialog.Show(this, "These images weren't the target's size and were resized for the move:\n\n" + string.Join("\n", resizedImages), "Moved, Images Resized", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Reload();
        if (swap && lib?.Mods.FirstOrDefault(x => x.FolderName == made) is Mod nm && lib.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod om)
            Change($"turn on \"{nm.Name}\" and off \"{om.Name}\"", () => { nm.Enabled = true; om.Enabled = false; return true; });
        SelectMod(made);
        status.Text = Ui.TitleCase($"Made \"{CrossMove.NewName(m, target)}\"") + (swap ? "  ·  Apply Changes to Put It in the Game" : "");
    }

    /// <summary>The plan in the Move window, then the new mod (CostumeMove.CreateMod); optionally on, with the original off.</summary>
    async void MoveCostume(Mod m, string file, Costume source, Costume target)
    {
        if (readOnly || lib == null || game == null || costumes == null) return;
        var (l, g, all) = (lib, game, costumes);
        var catalog = list.Catalog;
        UseWaitCursor = true;
        CostumeMove.Plan plan;
        try { plan = await Task.Run(() => CostumeMove.Make(m, file, source, target, all, g.Cooked, catalog)); }
        finally { UseWaitCursor = false; }
        var onTarget = l.Mods.Where(x => x.Manifest.UpkReplacements.Any(f => plan.Packages.Any(p => p.TargetFile.Equals(f, StringComparison.OrdinalIgnoreCase)))).ToList();
        bool swap;
        using (var f = new MoveCostumeForm(m, plan, catalog, onTarget))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            swap = f.SwapOn;
        }
        UseWaitCursor = true;
        string? error = null, made;
        var resized = new List<string>();
        try { made = await Task.Run(() => CostumeMove.CreateMod(l, m, plan, new Originals(l.DataFolder, g), out error, null, catalog, resized)); }
        finally { UseWaitCursor = false; }
        if (made == null) { Dialog.Show(this, error ?? "Unknown error.", "Not Moved", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (resized.Count > 0) Dialog.Show(this, "These images weren't the target's size and were resized for the move:\n\n" + string.Join("\n", resized), "Moved, Images Resized", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Reload();
        if (swap && lib?.Mods.FirstOrDefault(x => x.FolderName == made) is Mod nm && lib.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod om)
            Change($"turn on \"{nm.Name}\" and off \"{om.Name}\"", () => { nm.Enabled = true; om.Enabled = false; return true; });
        SelectMod(made);
        status.Text = Ui.TitleCase($"Made \"{CostumeMove.NewName(m, target)}\"") + (swap ? "  ·  Apply Changes to Put It in the Game" : "");
    }

    /// <summary>
    /// The tags menu of one mod. Its tags are listed with where they come from (automatic, from the mod, yours): unticking
    /// an automatic or mod tag hides it on this PC, ticking it again shows it. Then: add a tag, tag / untag every mod in
    /// the list, and rename or delete your own tags (automatic and mod tags can't be renamed).
    /// </summary>
    ContextMenuStrip TagsMenu(Mod m)
    {
        var menu = NewMenu();
        if (lib == null) return menu;
        var l = lib;
        var cmp = StringComparer.OrdinalIgnoreCase;
        var all = l.AllTags();
        menu.Items.Add(new ToolStripLabel($"Tags of \"{Short(m.Name)}\"") { ForeColor = Color.Gray });
        var own = m.Tags.Concat(m.HiddenTags.Where(h => m.AutoTags.Contains(h, cmp) || m.ModTags.Contains(h, cmp))).Distinct(cmp).ToList();
        foreach (string tag in own)
        {
            bool has = ModLibrary.HasTag(m, tag);
            string from = m.KindOf(tag) switch { Mod.TagKind.Auto => "automatic", Mod.TagKind.Mod => "from the mod", _ => "" };
            var item = new ToolStripMenuItem(tag + (from.Length > 0 ? $"      ({from}{(has ? "" : ", hidden here")})" : ""), null, (_, _) =>
                Change(has ? $"take tag \"{tag}\" off \"{m.Name}\"" : $"tag \"{m.Name}\" as \"{tag}\"", () =>
                {
                    if (has) ModLibrary.RemoveTag(m, tag); else ModLibrary.AddTag(m, tag);
                    return true;
                })) { Checked = has, Enabled = !readOnly };
            menu.Items.Add(item);
        }
        if (own.Count == 0) menu.Items.Add(new ToolStripMenuItem("(No Tags Yet)") { Enabled = false });

        var add = new ToolStripMenuItem("Add a Tag") { Enabled = !readOnly };
        add.DropDownItems.Add("New Tag", null, (_, _) =>
        {
            string? t = l.CleanTag(Ui.Prompt(this, "New Tag", $"Tag for \"{Short(m.Name)}\":", "", all));
            if (t != null) Change($"tag \"{m.Name}\" as \"{t}\"", () => { if (ModLibrary.HasTag(m, t)) return false; ModLibrary.AddTag(m, t); return true; });
        });
        var others = all.Where(t => !own.Contains(t, cmp)).ToList();
        if (others.Count > 0) add.DropDownItems.Add(new ToolStripSeparator());
        foreach (string tag in others)
            add.DropDownItems.Add(tag, null, (_, _) => Change($"tag \"{m.Name}\" as \"{tag}\"", () => { ModLibrary.AddTag(m, tag); return true; }));
        menu.Items.Add(add);

        var targets = shown.ToList();
        if (targets.Count > 1 && !readOnly)
        {
            menu.Items.Add(new ToolStripSeparator());
            var addAll = new ToolStripMenuItem($"Tag All {targets.Count} Mods in the List");
            void TagAll(string t) => Change($"tag {targets.Count} mods as \"{t}\"", () =>
            {
                bool any = targets.Any(x => !ModLibrary.HasTag(x, t));
                foreach (var x in targets) ModLibrary.AddTag(x, t);
                return any;
            });
            addAll.DropDownItems.Add("New Tag", null, (_, _) =>
            {
                string? t = l.CleanTag(Ui.Prompt(this, "Tag the List", $"Tag for all {targets.Count} mods in the list:", "", all));
                if (t != null) TagAll(t);
            });
            addAll.DropDownItems.Add(new ToolStripSeparator());
            foreach (string tag in all) addAll.DropDownItems.Add(tag, null, (_, _) => TagAll(tag));
            menu.Items.Add(addAll);
            var inList = targets.SelectMany(x => x.Tags).Distinct(cmp).Order(cmp).ToList();
            if (inList.Count > 0)
            {
                var remAll = new ToolStripMenuItem("Take a Tag Off the Mods in the List");
                foreach (string tag in inList)
                    remAll.DropDownItems.Add(tag, null, (_, _) => Change($"take tag \"{tag}\" off the list", () => { foreach (var x in targets) ModLibrary.RemoveTag(x, tag); return true; }));
                menu.Items.Add(remAll);
            }
        }
        var mine = l.Mods.SelectMany(x => x.UserTags).Distinct(cmp).Order(cmp).ToList();
        if (mine.Count > 0 && !readOnly)
        {
            menu.Items.Add(new ToolStripSeparator());
            var rename = new ToolStripMenuItem("Rename a User Tag");
            var delete = new ToolStripMenuItem("Delete a User Tag");
            foreach (string tag in mine)
            {
                rename.DropDownItems.Add(tag, null, (_, _) =>
                {
                    string? raw = Ui.Prompt(this, "Rename Tag", $"New name for \"{tag}\" (on every mod):", tag, all);
                    // Same name in other letter case = a case change (CleanTag would map it back to the old spelling).
                    string? t = raw == null ? null : raw.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase) ? raw.Trim() : l.CleanTag(raw);
                    if (t == null || t == tag) return;
                    Change($"rename tag \"{tag}\" to \"{t}\"", () =>
                    {
                        foreach (var x in l.Mods.Where(x => x.UserTags.Contains(tag, cmp)))
                        {
                            x.UserTags.RemoveAll(u => u.Equals(tag, StringComparison.OrdinalIgnoreCase));
                            ModLibrary.AddTag(x, t);
                        }
                        return true;
                    });
                });
                int users = l.Mods.Count(x => x.UserTags.Contains(tag, cmp));
                delete.DropDownItems.Add($"{tag}  ({users})", null, (_, _) =>
                    Change($"delete tag \"{tag}\" ({users} mod(s))", () => { foreach (var x in l.Mods) x.UserTags.RemoveAll(u => u.Equals(tag, StringComparison.OrdinalIgnoreCase)); return true; }));
            }
            menu.Items.Add(rename);
            menu.Items.Add(delete);
        }
        return menu;
    }

    /// <summary>"Apply Changes (N)" and a status note once the (hashing) plan is ready.</summary>
    void CountPending()
    {
        var (l, g) = (lib!, game!);
        string baseText = status.Text;
        pending = Task.Run(() => Applier.MakePlan(l, g, new Originals(l.DataFolder, g))).ContinueWith(t =>
        {
            if (t.IsFaulted || lib != l) return;
            int n = t.Result.Steps.Count, skipped = t.Result.Problems.Count;
            applyButton.Text = n == 0 ? "Apply Changes" : $"Apply Changes ({n})";
            status.Text = baseText + Ui.TitleCase((n == 0 ? "  ·  the game matches your list" : $"  ·  {n} file(s) to change") +
                          (skipped > 0 ? $"  ·  {skipped} skipped" : "") + (n + skipped > 0 ? "  ·  click for details" : ""));
            status.ForeColor = n == 0 ? Ui.Subtle : Ui.Text;
            // A user saw "7 Can't Be (See Apply)" and didn't know what it meant or whether Apply was safe: the line now
            // says it plainly and opens the plan (the Apply window asks before it writes anything).
            reviewable = n + skipped > 0;
            status.Cursor = reviewable ? Cursors.Hand : Cursors.Default;
            tips.SetToolTip(status, !reviewable ? "" :
                (n > 0 ? $"{n} game file(s) don't match your mod list yet. " : "") +
                (skipped > 0 ? $"{skipped} item(s) are skipped: files that can't be changed stay exactly as they are, and sound-pack lines that can't be added are left out (the rest of the pack still works). " : "") +
                "Click to see the list: the Apply window shows every file first, and nothing is written until you press Apply there.");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Toggle(Mod m) => Change($"turn {(m.Enabled ? "off" : "on")} \"{m.Name}\"", () => { m.Enabled = !m.Enabled; return true; });

    void MoveSelected(int delta, bool toEnd = false)
    {
        if (readOnly || lib == null || Selected is not Mod m || !ReorderView) return;
        // A marked group moves together (a user: arrow / fast-move the group).
        if (list.MarkedMods is { Count: > 1 } group)
        {
            var lg = lib;
            Change($"move {group.Count} mods {(toEnd ? (delta < 0 ? "to the top" : "to the bottom") : delta < 0 ? "up" : "down")}",
                () => toEnd ? lg.MoveGroupToEnd(group, delta) : lg.MoveGroupBy(group, delta));
            return;
        }
        int before = m.Priority;
        var l = lib;
        Change($"move \"{m.Name}\" {(toEnd ? (delta < 0 ? "to the top" : "to the bottom") : delta < 0 ? "up" : "down")}",
            () => (toEnd ? l.MoveToEnd(m, delta) : l.Move(m, delta)) && m.Priority != before);
    }

    /// <summary>Padlock: locks a mod at the top or bottom (it must be there, or next to a mod locked there), or unlocks it.</summary>
    void ToggleLock(Mod m)
    {
        if (readOnly || lib == null) return;
        var l = lib;
        string label = m.Lock != ModLock.None ? $"unlock \"{m.Name}\"" : $"lock \"{m.Name}\" at the {(l.CanLock(m) == ModLock.Top ? "top" : "bottom")}";
        Change(label, () => l.ToggleLock(m));
    }
}
