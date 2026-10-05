using System.Diagnostics;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The main window's list changes: undo / redo, a mod's note, tags.</summary>
sealed partial class MainForm
{
    // ---- Undo / redo (list changes only; Apply keeps its own history of game files)

    string StatePath => Path.Combine(lib!.DataFolder, "state.json");
    string ReadState() => File.Exists(StatePath) ? File.ReadAllText(StatePath) : "";
    void WriteState(string json) { string tmp = StatePath + ".tmp"; File.WriteAllText(tmp, json); File.Move(tmp, StatePath, overwrite: true); }

    /// <summary>Makes a list change as one undo step: <paramref name="change"/> edits the mods (false = nothing to do),
    /// then the state is saved and the list reloaded.</summary>
    bool Change(string label, Func<bool> change)
    {
        if (readOnly || lib == null) return false;
        string before = ReadState();
        if (!change()) return false;
        lib.SaveState();
        undo.Add((before, label));
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        note = Ui.TitleCase(label);
        Reload();
        return true;
    }


    void Undo()
    {
        if (readOnly || lib == null || undo.Count == 0) return;
        var (json, label) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add((ReadState(), label));
        WriteState(json);
        note = Ui.TitleCase($"Undone: {label}");
        Reload();
    }

    void Redo()
    {
        if (readOnly || lib == null || redo.Count == 0) return;
        var (json, label) = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add((ReadState(), label));
        WriteState(json);
        note = Ui.TitleCase($"Redone: {label}");
        Reload();
    }

    void UpdateUndo()
    {
        undoButton.Enabled = !readOnly && undo.Count > 0;
        redoButton.Enabled = !readOnly && redo.Count > 0;
        tips.SetToolTip(undoButton, Ui.Titled("Undo", undo.Count > 0 ? $"{undo[^1].Label}  (Ctrl+Z)" : "Nothing to undo. Turning mods on or off, moving, locking and tags can be undone."));
        tips.SetToolTip(redoButton, Ui.Titled("Redo", redo.Count > 0 ? $"{redo[^1].Label}  (Ctrl+Y)" : "Nothing to redo."));
    }

    /// <summary>The control with the keyboard focus, inside nested containers (a text box in the editor, the note box …).</summary>
    Control? FocusedLeaf()
    {
        Control? a = ActiveControl;
        while (a is ContainerControl cc && cc.ActiveControl != null) a = cc.ActiveControl;
        return a;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // F1: the manual, at the section for the tab shown.
        if (keyData == Keys.F1) { HelpForm.Show(this, settings, pages.SelectedIndex switch { 1 => "editor", 2 => "extract", _ => "contents" }); return true; }
        // F11: the 3D preview full screen (its window handles F11 / Esc to come back).
        if (keyData == Keys.F11 && pages.SelectedIndex == 0) { storePreview.ToggleFull(); return true; }
        // Ctrl+Enter: Apply Changes (Kurt), anywhere on the Mods tab.
        if (keyData == (Keys.Control | Keys.Enter) && pages.SelectedIndex == 0 && applyButton.Enabled && applyButton.Visible)
        {
            Apply();
            return true;
        }
        if (pages.SelectedIndex == 0 && FocusedLeaf() is not TextBoxBase)
        {
            // Esc: pauses the 3D preview's animation while one plays (Kurt); otherwise Esc does what it did.
            if (keyData == Keys.Escape && storePreview.PausePlayback()) return true;
            // P: play / pause the 3D preview (Kurt, 2026-10-04: one key in every 3D view)
            if (keyData == Keys.P && storePreview.TogglePlayback()) return true;
            // Del: Remove Mod (Kurt), which asks first as the button does.
            if (keyData == Keys.Delete && Selected is Mod && !readOnly) { RemoveMod(); return true; }
            // Ctrl+Up / Down: priority one step; Ctrl+Home / End: to the top / bottom (a marked group moves together).
            if (keyData == (Keys.Control | Keys.Up)) { MoveSelected(-1); return true; }
            if (keyData == (Keys.Control | Keys.Down)) { MoveSelected(1); return true; }
            if (keyData == (Keys.Control | Keys.Home)) { MoveSelected(-1, toEnd: true); return true; }
            if (keyData == (Keys.Control | Keys.End)) { MoveSelected(1, toEnd: true); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Create Post for an installed mod: the post saved in it, kept in its Post\ folder (no reload needed).</summary>
    void CreatePost(Mod m)
    {
        using var f = new PostForm(PostWriter.From(m), m.Folder, ModPost.Read(m.Folder), (n, d, imgs) => ModPost.Write(m.Folder, n, d, imgs));
        f.ShowDialog(this);
    }

    // ---- Note (under the store image)

    void ShowNote(Mod? m)
    {
        noteMod = m;
        noteBox.Text = (m?.Note ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
        noteBox.ReadOnly = readOnly || m == null;
        noteSource.Text = m == null ? "" : m.LocalNote != null ? (m.Manifest.Notes != null ? "User (Replaces the Mod's)" : "User") : m.Manifest.Notes != null ? "From the Mod" : "None Yet: Type to Add One";
        noteReset.Visible = m?.LocalNote != null && m.Manifest.Notes != null;
    }

    /// <summary>Keeps what's typed in the note box as the user's note (one undo step), or drops the user's note when the
    /// text is the mod's own again. No reload: the list doesn't show notes.</summary>
    void SaveNote()
    {
        if (readOnly || lib == null || noteMod == null) return;
        var m = lib.Find(noteMod.FolderName);
        if (m == null) return;
        string text = noteBox.Text.Replace("\r\n", "\n").TrimEnd();
        if (text == m.Note.Replace("\r\n", "\n").TrimEnd()) return;
        string? local = text == (m.Manifest.Notes ?? "").Replace("\r\n", "\n").TrimEnd() ? null : text;
        string before = ReadState();
        m.LocalNote = local;
        lib.SaveState();
        undo.Add((before, $"edit the note of \"{m.Name}\""));
        redo.Clear();
        UpdateUndo();
        ShowNote(m);
    }

    void ResetNote()
    {
        if (readOnly || lib == null || noteMod == null || lib.Find(noteMod.FolderName) is not Mod m) return;
        Change($"use the mod's note for \"{m.Name}\"", () => { if (m.LocalNote == null) return false; m.LocalNote = null; return true; });
    }

    // ---- Tags

    static string Short(string name) => name.Length > 40 ? name[..40] + "…" : name;

    /// <summary>Turns every mod in the list (as filtered, open groups only) on, or off when all are on already.</summary>
    void SetAllVisible()
    {
        var targets = shown.ToList();
        if (targets.Count == 0) return;
        bool turnOn = targets.Any(m => !m.Enabled);
        Change($"turn {(turnOn ? "on" : "off")} {targets.Count} mod(s) in the list", () =>
        {
            foreach (var m in targets) m.Enabled = turnOn;
            return true;
        });
    }

    /// <summary>A card's right-click menu: on/off, edit, export, then the tags.</summary>
    ContextMenuStrip CardMenu(Mod m)
    {
        var menu = TagsMenu(m);
        int at = 0;
        menu.Items.Insert(at++, new ToolStripMenuItem(m.Enabled ? "Turn Off" : "Turn On", null, (_, _) => Toggle(m)) { Enabled = !readOnly });
        menu.Items.Insert(at++, new ToolStripMenuItem("Edit", null, (_, _) => EditMod(m)) { Enabled = !readOnly });
        menu.Items.Insert(at++, new ToolStripMenuItem("Export to ZIP", null, (_, _) => ExportMod()));
        menu.Items.Insert(at++, new ToolStripMenuItem("Update from a File", null, (_, _) => UpdateFromFile(m)) { Enabled = !readOnly });
        menu.Items.Insert(at++, new ToolStripMenuItem("Create Post", null, (_, _) => CreatePost(m)));
        menu.Items.Insert(at++, NexusMenu(m));
        menu.Items.Insert(at++, CardPictureMenu(m));
        if (SingleCostume(m) != null)
        {
            var move = new ToolStripMenuItem("Move to Another Costume") { Enabled = !readOnly };
            FillCostumeItems(move.DropDownItems, m);
            menu.Items.Insert(at++, move);
        }
        menu.Items.Insert(at, new ToolStripSeparator());
        return menu;
    }

    /// <summary>
    /// Right-click → Card Picture (Kurt): which of the mod's own images its card in the list shows, each with a small
    /// thumbnail; Automatic = the usual pick (hero portrait, costume icon, store image …). One undo step.
    /// </summary>
    ToolStripMenuItem CardPictureMenu(Mod m)
    {
        var item = new ToolStripMenuItem("Card Picture") { Enabled = !readOnly };
        string autoText = m.Manifest.CardPicture != null ? $"The Mod's Choice ({m.Manifest.CardPicture})" : "Automatic (Hero Portrait, Costume Icon, Store Image)";
        var auto = new ToolStripMenuItem(autoText, null, (_, _) =>
            Change($"Card picture of \"{m.Name}\": the mod's choice", () => { if (m.LocalCard == null) return false; m.LocalCard = null; return true; })) { Checked = m.LocalCard == null };
        item.DropDownItems.Add(auto);
        var candidates = m.CardCandidates();
        // A picture of your own (a user's request): copied into data\pictures, kept on this PC; Export can put it into the mod.
        var own = new ToolStripMenuItem("Custom Image", null, (_, _) =>
        {
            using var d = new OpenFileDialog { Title = $"Card Picture for {m.Name}", Filter = ModPictures.DialogFilter };
            if (d.ShowDialog(this) != DialogResult.OK || lib == null) return;
            string key;
            try { key = ModPictures.KeepLocal(lib.DataFolder, m.FolderName, d.FileName, "card"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialog.Show(this, ex.Message, "Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            Change($"Card picture of \"{m.Name}\": {Path.GetFileName(d.FileName)}", () => { m.LocalCard = key; return true; });
        })
        { Checked = ModPictures.IsFile(m.LocalCard) && !candidates.Any(c => c.Texture.Equals(m.LocalCard, StringComparison.OrdinalIgnoreCase)) };
        if (ModPictures.Resolve(m.Folder, m.LocalCard) is string ownFile && own.Checked) try { own.Image = Ui.DdsThumb(ownFile, (int)(32 * DeviceDpi / 96f)); own.ImageScaling = ToolStripItemImageScaling.None; } catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { }
        item.DropDownItems.Add(own);
        if (candidates.Count == 0) return item;
        item.DropDownItems.Add(new ToolStripSeparator());
        int px = (int)(32 * DeviceDpi / 96f);
        string? current = m.LocalCard;
        foreach (var (tex, file) in candidates)
        {
            string t = tex;
            var pick = new ToolStripMenuItem(ModPictures.IsFile(tex) ? ModPictures.Label(tex) + "  ·  the Mod's Own Picture" : tex, null, (_, _) =>
                Change($"Card picture of \"{m.Name}\": {t}", () => { if (t.Equals(m.LocalCard, StringComparison.OrdinalIgnoreCase)) return false; m.LocalCard = t; return true; }))
            { Checked = tex.Equals(current, StringComparison.OrdinalIgnoreCase), ImageScaling = ToolStripItemImageScaling.None };
            try { if (Ui.DdsThumb(file, px) is Image img) pick.Image = img; } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OutOfMemoryException) { }
            item.DropDownItems.Add(pick);
        }
        return item;
    }
}
