using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Gear page (Kurt, 2026-11-17: "give people the ability to change the gear names and icons"): a hero's five gear slots,
/// each with its name (an English string in the draft, as Rename Power does) and its icon (the inventory's 40 × 40 and the
/// large 128 × 128, put in as icon replacements: the Icons tab shows them). Icons come from a picture, or from the 3D view
/// (Make Icon From 3D View, as the power icons: the character in its idle pose, no power effects, on transparency).
/// What a slot shares with other heroes (a "Boots" name string, Spider-Man's icons) is said up front: it changes there too.
/// </summary>
sealed partial class ModEditorView
{
    StorePreview? gearPreview;
    DropDown? gearHeroBox;
    TableLayoutPanel? gearSlotsTable;
    Label? gearNote;
    Dictionary<string, List<GearSlot>>? gearAll;
    List<string> gearHeroes = [];
    Dictionary<ulong, string>? gearOriginalNames;
    readonly List<(GearSlot Slot, PictureBox Pic, TextBox Name, List<(ulong Id, TextBox Box)> Types, Label Note, Button GameIcon, Button GameName)> gearRows = [];

    Control GearPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, (int)(6 * S), 0, 0) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // left: the 3D view (made when the page first shows, as on the Powers page: the theme can't touch its surface)
        var host = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        if (editing == null || game == null)
            host.Controls.Add(new Label { Text = "Save the mod once, then come back here to make icons from its 3D view. Names and pictures can be changed now.", AutoSize = true, MaximumSize = new Size((int)(420 * S), 0), Tag = "subtle", Padding = new Padding(8) });
        page.Controls.Add(host, 0, 0);

        // right: the hero, then the slots
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding((int)(12 * S), 0, 0, 0), Margin = new Padding(0) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heroRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, (int)(4 * S)) };
        heroRow.Controls.Add(new Label { Text = "HERO", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, (int)(8 * S), (int)(8 * S), 0) });
        gearHeroBox = new DropDown { Width = (int)(260 * S), Margin = new Padding(0, (int)(2 * S), 0, 0) };
        Ui.Tip(gearHeroBox, "Whose gear to change. It starts on this mod's hero; the changes go into this mod either way.");
        heroRow.Controls.Add(gearHeroBox);
        right.Controls.Add(heroRow, 0, 0);
        var hint = new Label
        {
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, (int)(8 * S)), MaximumSize = new Size((int)(560 * S), 0),
            Text = "Each slot's name and icon, for every costume of the hero and every item of that slot. They go into this mod as a string and icon replacements (the Strings and Icons tabs show them). Some slots share their name or icon with other heroes: it changes there too.",
        };
        right.Controls.Add(hint, 0, 1);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        gearSlotsTable = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        gearSlotsTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); gearSlotsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(gearSlotsTable);
        right.Controls.Add(scroll, 0, 2);
        gearNote = new Label { AutoSize = true, Tag = "subtle", Margin = new Padding(0, (int)(6 * S), 0, 0) };
        right.Controls.Add(gearNote, 0, 3);
        page.Controls.Add(right, 1, 0);

        bool filled = false;
        page.VisibleChanged += (_, _) =>
        {
            if (!page.Visible) return;
            if (!filled) { filled = true; FillGearHeroes(); }
            if (gearPreview == null && editing != null && game != null)
            {
                gearPreview = new StorePreview { Dock = DockStyle.Fill, CookedFolder = game.Cooked, Catalog = catalog, PowerStrip = false, Always3D = true, NoEffects = true };
                gearPreview.ControlAdded += (_, _) => Ui.RestyleButtons(gearPreview);
                host.Controls.Add(gearPreview);
                gearPreview.Mod = editing;
                GearIdle();
            }
        };
        return Page(page, Toolbar(), "Rename a hero's gear and give it new icons (from a picture or from the 3D view).");
    }

    /// <summary>The heroes in the drop-down, this mod's (or the one its changes are for) picked.</summary>
    void FillGearHeroes()
    {
        if (gearHeroBox == null) return;
        if (PowerDbNow() is not { } db)
        {
            if (gearNote != null) gearNote.Text = "The game folder isn't set, so the gear can't be read.";
            return;
        }
        var hd = catalog?.EntriesFor("ICO__MarvelUIIcons_HD_SF.upk")?.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        gearAll = GearData.All(db, hd);
        gearHeroes = [.. gearAll.Keys.OrderBy(GearData.HeroName, StringComparer.OrdinalIgnoreCase)];
        gearHeroBox.Items.Clear();
        foreach (string h in gearHeroes) gearHeroBox.Items.Add(GearData.HeroName(h));
        // the mod's own hero when its gear is changed (or nothing is), else the hero whose gear the mod changes most: an icon counts
        // more than a name (a shared name string, Storm's and Luke Cage's "Wristbands", shows as changed for both)
        string? own = GearData.HeroOf(draft.Packages.Select(p => p.File), gearHeroes);
        int Score(string h) => gearAll[h].Sum(s => (GearData.Textures(s).Any(t => HasIconReplacement(t.Package, t.Texture)) ? 2 : 0) + (GearChanged(s) ? 1 : 0));
        string? start = own != null && (Score(own) > 0 || !gearHeroes.Any(h => Score(h) > 0)) ? own
            : gearHeroes.Where(h => Score(h) > 0).OrderByDescending(Score).FirstOrDefault() ?? own;
        gearHeroBox.SelectedIndexChanged += (_, _) => ShowGearSlots();
        gearHeroBox.SelectedIndex = Math.Max(0, start != null ? gearHeroes.IndexOf(start) : 0);
        ShowGearSlots();
    }

    bool GearChanged(GearSlot s) =>
        GearData.Textures(s).Any(t => HasIconReplacement(t.Package, t.Texture)) || GearTextChanged(s.NameId) || GearTextChanged(s.TypeId)
        || (s.Unique == null && powerDb != null && GearData.TierGroups(powerDb, s).Any(g => GearTextChanged(g.TypeId)));

    /// <summary>A fold-open heading: a big ▸ / ▾ (Kurt, 2026-11-17: 50 % bigger) and its caption; a click on either opens or closes <paramref name="body"/>.</summary>
    Control FoldHead(Control body, Func<string> caption, string tip)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, (int)(4 * S), 0, 2), Cursor = Cursors.Hand };
        // (drawn: the font's ▸ stays tiny at any size)
        int cs = (int)(16 * S);
        var caret = new Panel { Size = new Size(cs, cs), Margin = new Padding(0, (int)(1 * S), (int)(4 * S), 0), Cursor = Cursors.Hand, BackColor = Color.Transparent };
        caret.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float m = cs * 0.2f, w = cs - 2 * m;
            PointF[] tri = body.Visible
                ? [new(m, m + w * 0.15f), new(m + w, m + w * 0.15f), new(m + w / 2, m + w * 0.85f)]   // ▾
                : [new(m + w * 0.15f, m), new(m + w * 0.85f, m + w / 2), new(m + w * 0.15f, m + w)];  // ▸
            using var br = new SolidBrush(Ui.Subtle);
            e.Graphics.FillPolygon(br, tri);
        };
        var text = new Label { AutoSize = true, Tag = "subtle", Font = Ui.Bold(7.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(3 * S), 0, 0), Cursor = Cursors.Hand };
        void Show() { caret.Invalidate(); text.Text = caption(); }
        Show();
        foreach (Control c in new Control[] { row, caret, text }) { c.Click += (_, _) => { body.Visible = !body.Visible; Show(); }; Ui.Tip(c, tip); }
        row.Controls.Add(caret); row.Controls.Add(text);
        return row;
    }

    /// <summary>
    /// All Five Slots at Once (Kurt, 2026-11-17: "can we have the list once instead of updating each one 5 times"): a prefix per
    /// item level, written into every standard slot as "&lt;prefix&gt; &lt;piece&gt;" (Son of Ultron's → Son of Ultron's Gloves,
    /// … Cape). The piece is the words a slot's game lines all end with. Shown when every slot has the same number of levels
    /// and a piece word; a level's box is empty where the slots' prefixes differ now. Only 10 of 64 heroes use the same
    /// prefixes in every slot in the game (a weapon slot often has its own), so the per-slot lists stay for fine-tuning.
    /// </summary>
    Control? AllSlotsLevels(string hero)
    {
        if (gearAll == null || PowerDbNow() is not { } db) return null;
        var std = gearAll[hero].Where(x => x.Unique == null).OrderBy(x => x.Number).ToList();
        var tiers = GearData.Tiers(db);
        var lists = std.Select(x => tiers.TryGetValue((hero.ToLowerInvariant(), x.Number), out var l) ? l : []).ToList();
        if (std.Count < 2 || lists.Any(l => l.Count < 2 || l.Count != lists[0].Count)) return null;
        // each slot's piece: the last words all of its game lines share ("Belt", "Bo Staff")
        var pieces = new List<string>();
        foreach (var l in lists)
        {
            var words = l.Select(t => GearOriginalText(t.TypeId).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
            int k = 0;
            while (words.All(w => w.Length > k + 1) && words.Select(w => w[^(k + 1)]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1) k++;
            if (k == 0) return null;
            pieces.Add(string.Join(" ", words[0][^k..]));
        }
        int n = lists[0].Count;
        string? Prefix(int si, int i)
        {
            string t = GearText(lists[si][i].TypeId), end = " " + pieces[si];
            return t.EndsWith(end, StringComparison.OrdinalIgnoreCase) ? t[..^end.Length] : null;
        }
        var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, (int)(10 * S)) };
        var body = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Visible = false, Margin = new Padding((int)(4 * S), 0, 0, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var note = new Label { AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(620 * S), 0), Margin = new Padding(0, 0, 0, (int)(6 * S)),
            Text = $"All Levels copies one prefix into every level's box (Enter); then Fill All Slots writes them. Type a level's prefix once: it's written into every slot as \"<prefix> <piece>\" ({string.Join(", ", pieces)}). An empty box: the slots differ now (open a slot's own list below to see them). Clear a box and press Enter: that level back to the game's in every slot." };
        body.Controls.Add(note, 0, 0); body.SetColumnSpan(note, 2);
        var commits = new List<(TextBox Box, Action Write)>();
        // All Levels (Kurt, 2026-11-17): one prefix copied into every level's box below (nothing is written yet); Fill All Slots
        // then writes them, or change single levels first
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(new Label { Text = "All Levels", AutoSize = true, Font = Ui.Bold(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(2 * S), (int)(10 * S), (int)(6 * S)) }, 0, 1);
        var allLevels = new TextBox { Width = (int)(320 * S), Margin = new Padding(3, 3, 3, (int)(8 * S)) };
        Ui.Tip(allLevels, $"One prefix for all {n} levels: Enter copies it into every level's box below (nothing is written yet). Then change single levels if you like and click Fill All Slots to write them into all {std.Count} slots.");
        allLevels.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            foreach (var (b, _) in commits) b.Text = allLevels.Text.Trim();
        };
        body.Controls.Add(allLevels, 1, 1);
        for (int i = 0; i < n; i++)
        {
            int lvl = i;
            var ps = Enumerable.Range(0, std.Count).Select(si => Prefix(si, lvl)).ToList();
            string common = ps.All(x => x != null) && ps.Distinct().Count() == 1 ? ps[0]! : "";
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(new Label { Text = $"Level {i + 1:00}", AutoSize = true, Tag = "subtle", Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(2 * S), (int)(10 * S), 0) }, 0, i + 2);
            var t = new TextBox { Width = (int)(320 * S), Text = common };
            Ui.Tip(t, $"Level {i + 1}'s prefix for all {std.Count} slots: {string.Join(", ", Enumerable.Range(0, std.Count).Select(si => GearText(lists[si][lvl].TypeId)))} now. Typing \"Son of Ultron's\" makes them {string.Join(", ", pieces.Select(pc => "Son of Ultron's " + pc))}. Press Enter or leave the box to write them; empty + Enter = the game's in every slot.");
            string last = common;
            void Commit()
            {
                string pf = t.Text.Trim();
                if (pf == last) return;
                last = pf;
                for (int si = 0; si < std.Count; si++)
                    SetGearString(std[si], lists[si][lvl].TypeId, pf.Length == 0 ? "" : pf + " " + pieces[si]);
            }
            t.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); } };
            t.Leave += (_, _) => Commit();
            body.Controls.Add(t, 1, i + 2);
            commits.Add((t, () => { if (t.Text.Trim().Length > 0) { last = null!; Commit(); } }));
        }
        // Fill All Slots (Kurt, 2026-11-17): every level with a prefix written into all the slots at once
        var fill = Ui.AccentButton("Fill All Slots", () =>
        {
            foreach (var (_, c) in commits) c();   // (each row's boxes and notes follow at once; the list stays open)
        }, tip: $"Write every level that has a prefix into all {std.Count} slots at once (\"<prefix> <piece>\": {string.Join(", ", pieces)}), as Enter on each box would. Empty boxes are left as they are.");
        fill.Margin = new Padding(0, (int)(8 * S), 0, (int)(4 * S));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(fill, 1, n + 2);
        int same = Enumerable.Range(0, n).Count(i => Prefix(0, i) != null && Enumerable.Range(0, std.Count).Select(si => Prefix(si, i)).Distinct().Count() == 1);
        box.Controls.Add(FoldHead(body, () => $"ALL FIVE SLOTS AT ONCE  ·  {n} levels  ·  {same} the same in every slot",
            $"Click to open or close: one prefix per item level, written into all {std.Count} slots (prefix + {string.Join(" / ", pieces)})."));
        box.Controls.Add(body);
        return box;
    }

    bool GearTextChanged(ulong id) => id != 0 && draft.Strings.Any(x => x.Language == "eng" && x.Id == id);
    string GearOriginalText(ulong id)
    {
        gearOriginalNames ??= catalog?.Strings("eng").GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Text) ?? [];
        return gearOriginalNames.TryGetValue(id, out var t) ? t : "";
    }
    string GearText(ulong id) => (id != 0 ? draft.Strings.FirstOrDefault(x => x.Language == "eng" && x.Id == id)?.Text : null) ?? GearOriginalText(id);

    string GearOriginalName(GearSlot s)
    {
        gearOriginalNames ??= catalog?.Strings("eng").GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Text) ?? [];
        return gearOriginalNames.TryGetValue(s.NameId, out var t) ? t : "";
    }

    string GearName(GearSlot s) => draft.Strings.FirstOrDefault(x => x.Language == "eng" && x.Id == s.NameId && s.NameId != 0)?.Text ?? GearOriginalName(s);

    void ShowGearSlots()
    {
        if (gearSlotsTable == null || gearAll == null || gearHeroBox == null || gearHeroBox.SelectedIndex < 0 || gearHeroBox.SelectedIndex >= gearHeroes.Count) return;
        string hero = gearHeroes[gearHeroBox.SelectedIndex];
        gearSlotsTable.SuspendLayout();
        foreach (Control c in gearSlotsTable.Controls.Cast<Control>().ToList()) { (c as PictureBox)?.Image?.Dispose(); c.Dispose(); }
        gearSlotsTable.Controls.Clear();
        gearSlotsTable.RowStyles.Clear();
        gearRows.Clear();
        int row = 0;
        bool uniquesHead = false;
        if (AllSlotsLevels(hero) is { } all)
        {
            gearSlotsTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            gearSlotsTable.Controls.Add(all, 0, row); gearSlotsTable.SetColumnSpan(all, 2);
            row++;
        }
        foreach (var slot in gearAll[hero])
        {
            // the hero's unique items after the five slots (Kurt, 2026-11-17)
            if (slot.Unique != null && !uniquesHead)
            {
                uniquesHead = true;
                var head = new Label
                {
                    AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(640 * S), 0), Margin = new Padding(0, (int)(14 * S), 0, (int)(4 * S)),
                    Text = "UNIQUE ITEMS  ·  each has its own name and inventory icon (40 × 40); most show their slot's large icon in the tooltip, and that one is the slot's own (changed with the slot above).",
                };
                gearSlotsTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                gearSlotsTable.Controls.Add(head, 0, row); gearSlotsTable.SetColumnSpan(head, 2);
                row++;
            }
            string what = slot.Unique != null ? "unique item" : "slot";
            var pic = new PictureBox { Size = new Size((int)(80 * S), (int)(80 * S)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 22, 26), Margin = new Padding(0, (int)(6 * S), (int)(12 * S), (int)(10 * S)), Cursor = Cursors.Hand };
            Ui.Tip(pic, $"This {what}'s icon as the mod has it (the large one where it has one). Click: open it in the icon maker to change its background, glow, colors, outline or framing (as it was made, for an icon made here).");
            var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, (int)(4 * S), 0, (int)(10 * S)) };
            // a slot: "SLOT 5 · Cape" (its name is the game's generic word, locked: Kurt, 2026-11-17); a unique: its name in a box below
            var heading = new Label { Text = slot.Unique != null ? $"UNIQUE  ·  SLOT {slot.Number}  ·  {slot.Unique.ToUpperInvariant()}" : $"SLOT {slot.Number}  ·  {GearName(slot)}", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, 0, 0, 2) };
            if (slot.Unique == null) Ui.Tip(heading, $"Slot {slot.Number}'s name in game ({GearName(slot)}): the game's word for this kind of gear, often shared with other heroes, so it isn't changed here. The second line by item level below is this slot's own (Son of Ultron's Belt – Sentry).");
            col.Controls.Add(heading);
            // a slot's name is locked (Kurt, 2026-11-17): it's the generic word ("Belt", "Boots") many heroes share; the flavor is the
            // second line, per item level. A unique's name is its own flavor text: editable unless another item shares it.
            bool nameLocked = slot.Unique == null || GearData.SharingName(gearAll, slot).Count > 0;
            var name = new TextBox { Width = (int)(320 * S), Text = GearName(slot), Enabled = slot.NameId != 0, ReadOnly = nameLocked };
            if (nameLocked) name.ForeColor = Ui.Subtle;
            Ui.Tip(name, slot.NameId == 0 ? $"This {what} has no name of its own in the game data."
                : slot.Unique == null ? $"The slot's name ({GearOriginalName(slot)}) is locked: it's the generic word the game uses for this kind of gear, often shared with other heroes. Change the second line by item level below instead (Son of Ultron's Belt – Sentry)."
                : nameLocked ? $"This unique item's name ({GearOriginalName(slot)}) is shared with another item, so it's locked: renaming it would rename that one too."
                : $"The {what}'s name in game (English). The game's own: {GearOriginalName(slot)}. Press Enter or leave the box to keep it; empty = the game's.");
            var s0 = slot;
            pic.Click += (_, _) => ReopenGearIcon(s0);
            if (!nameLocked)
            {
                name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SetGearName(s0, name.Text); } };
                name.Leave += (_, _) => SetGearName(s0, name.Text);
            }
            if (slot.Unique != null) col.Controls.Add(name);   // (a slot's name is in its heading)
            // the tooltip's second line (Kurt, 2026-11-17: "Son of Ultron's Belt – Sentry"): a unique's own ("Unique – …"); a slot's
            // per item version (Belt01 … Belt15 by level, each its own ItemCategory), in a list that folds open
            var types = new List<(ulong Id, TextBox Box)>();
            TextBox TypeBox(ulong id, string tip)
            {
                var box = new TextBox { Width = (int)(320 * S), Text = GearText(id) };
                Ui.Tip(box, tip);
                box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SetGearString(s0, id, box.Text); } };
                box.Leave += (_, _) => SetGearString(s0, id, box.Text);
                types.Add((id, box));
                return box;
            }
            if (slot.Unique != null)
            {
                col.Controls.Add(new Label { Text = "SECOND LINE  ·  \"Unique – …\"", AutoSize = true, Tag = "subtle", Font = Ui.Bold(7.5f), Margin = new Padding(0, (int)(4 * S), 0, 1) });
                if (slot.TypeId != 0)
                    col.Controls.Add(TypeBox(slot.TypeId, $"The line under the item's name: \"Unique – {GearOriginalText(slot.TypeId)}\" in game; this is the part after \"Unique – \" (the game's own: {GearOriginalText(slot.TypeId)}). Press Enter or leave the box to keep it; empty = the game's."));
            }
            else if (PowerDbNow() is { } gdb && GearData.TierGroups(gdb, slot) is { Count: > 0 } groups)
            {
                var levels = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, (int)(2 * S)), Visible = false };
                levels.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); levels.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                int lr = 0;
                foreach (var (tid, items) in groups)
                {
                    var users = GearData.TypeUsers(gdb, tid, slot.Hero, slot.Number);
                    levels.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    // (fixed cells: the theme wraps each box in a frame afterwards, which reshuffled cells placed in order)
                    levels.Controls.Add(new Label { Text = items, AutoSize = true, Tag = "subtle", Anchor = AnchorStyles.Left, Margin = new Padding(0, (int)(2 * S), (int)(10 * S), 0) }, 0, lr);
                    levels.Controls.Add(TypeBox(tid, $"The line under the name on {items}: \"{GearOriginalText(tid)} – <hero>\" in game (Son of Ultron's Belt – Sentry); this is the part before the hero's name (the game's own: {GearOriginalText(tid)})."
                        + (users.Count > 0 ? $" Shared with {string.Join(", ", users)}: changing it changes theirs too." : "") + " Press Enter or leave the box to keep it; empty = the game's."), 1, lr);
                    lr++;
                }
                int changedLines = groups.Count(g => GearTextChanged(g.TypeId));
                col.Controls.Add(FoldHead(levels, () => $"SECOND LINE BY ITEM LEVEL  ·  {groups.Count} names (\"… – Hero\"){(changedLines > 0 ? $"  ·  {changedLines} changed" : "")}",
                    $"Click to open or close: the line under this slot's name for each of its {groups.Sum(g => g.Items.Split(',').Length)} item versions (the game gives each level of item its own: Light Belt, … Son of Ultron's Belt, … Uncanny Belt)."));
                col.Controls.Add(levels);
            }
            // (wraps when the row is narrow: a unique item has one more button, Copy From Slot)
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Math.Max((int)(420 * S), gearSlotsTable.Parent?.ClientSize.Width - (int)(130 * S) ?? 0), 0), Margin = new Padding(0, (int)(4 * S), 0, 0) };
            buttons.Controls.Add(Ui.FlatButton("Replace Icon", () => { if (PickIconImage() is string img && PutGearIcon(s0, img)) ShowGearSlots(); },
                tip: "Use a picture of your own (.PNG, .JPG, .WEBP, .DDS …) as this slot's icon: it's converted to the game's sizes (40 × 40 and the large 128 × 128). Keep the background transparent, as the game's gear icons are."));
            buttons.Controls.Add(Ui.FlatButton("Make Icon From 3D View", () => MakeGearIcon(s0),
                tip: "Make the icon from the 3D view on the left (the character in its idle pose, no power effects): turn and zoom in on this piece of gear, then take a snapshot. The background is transparent unless you pick one."));
            var gameIcon = Ui.FlatButton("Game's Icon", () => { GearIconOff(s0); ShowGearSlots(); },
                tip: "Take this slot's icon back to the game's own.");
            var gameName = Ui.FlatButton("Game's Name", () => { SetGearName(s0, ""); foreach (var (tid, _) in types.ToList()) SetGearString(s0, tid, ""); }, tip: "Take this one's name and its second lines back to the game's own.");
            // a unique: start its icon from its standard slot's (Kurt, 2026-11-17)
            if (slot.Unique != null && gearAll[hero].FirstOrDefault(o => o.Unique == null && o.Number == slot.Number) is { } std)
                buttons.Controls.Add(Ui.FlatButton($"Copy From Slot {slot.Number}", () => ReopenGearIcon(s0, std),
                    tip: $"Open the icon maker for this unique item, starting from slot {slot.Number}'s icon ({GearName(std)}): an icon made here comes with its picture on transparency and its settings (background, outline, glow, colors) to change, not baked in, so you can give it another outline color and so on. Use as Icon there makes it this item's icon; slot {slot.Number}'s stays as it is."));
            buttons.Controls.Add(gameIcon); buttons.Controls.Add(gameName);
            col.Controls.Add(buttons);
            var note = new Label { AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(520 * S), 0), Margin = new Padding(0, (int)(4 * S), 0, 0) };
            col.Controls.Add(note);
            gearSlotsTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            gearSlotsTable.Controls.Add(pic, 0, row); gearSlotsTable.Controls.Add(col, 1, row);
            gearRows.Add((slot, pic, name, types, note, gameIcon, gameName));
            row++;
        }
        Theme.ApplyTree(gearSlotsTable, Palette.Dark); Modern.Modernize(gearSlotsTable); Ui.Restyle(gearSlotsTable);
        gearSlotsTable.ResumeLayout();
        foreach (var r in gearRows) ShowGearRow(r);
        int slots = gearAll[hero].Count(s => s.Unique == null), uniques = gearAll[hero].Count - slots;
        if (gearNote != null) gearNote.Text = gearAll[hero].Count == 0 ? "This hero has no gear in the game data." : $"{GearData.HeroName(hero)}: {slots} gear slots and {uniques} unique items, {gearAll[hero].Count(GearChanged)} changed in this mod.";
    }

    void ShowGearRow((GearSlot Slot, PictureBox Pic, TextBox Name, List<(ulong Id, TextBox Box)> Types, Label Note, Button GameIcon, Button GameName) r)
    {
        var old = r.Pic.Image; r.Pic.Image = GearIconImage(r.Slot); old?.Dispose();
        bool iconChanged = GearData.Textures(r.Slot).Any(t => HasIconReplacement(t.Package, t.Texture));
        bool nameChanged = GearTextChanged(r.Slot.NameId) || r.Types.Any(t => GearTextChanged(t.Id));
        r.GameIcon.Enabled = iconChanged; r.GameName.Enabled = nameChanged;
        var lines = new List<string>();
        if (iconChanged || nameChanged) lines.Add("Changed in this mod: " + string.Join(" and ", new[] { nameChanged ? "name" : null, iconChanged ? "icon" : null }.Where(x => x != null)) + ".");
        if (gearAll != null)
        {
            string Who(List<GearSlot> l) => string.Join(", ", l.Select(o => o.Hero.Equals(r.Slot.Hero, StringComparison.OrdinalIgnoreCase) ? $"slot {o.Number}" : GearData.HeroName(o.Hero)).Distinct());
            var names = GearData.SharingName(gearAll, r.Slot);
            if (names.Count > 0 && r.Slot.Unique != null) lines.Add($"Its name is shared with {Who(names)}, so it's locked here (renaming it would rename theirs too).");
            var types = GearData.SharingType(gearAll, r.Slot);
            if (types.Count > 0) lines.Add($"Its second line is shared with {Who(types)}: changing it changes theirs too.");
            var icons = GearData.SharingIcon(gearAll, r.Slot);
            if (icons.Count > 0) lines.Add($"Its icon is shared with {Who(icons)}: a new icon shows there too.");
        }
        if (r.Slot.HdFromSlot != null) lines.Add($"Its large tooltip icon is its slot's own ({r.Slot.HdFromSlot}): only its 40 × 40 inventory icon is changed here.");
        else if (r.Slot.HdTexture == null) lines.Add("No large icon in the game for this one: only the 40 × 40 icon is changed.");
        r.Note.Text = string.Join("\n", lines);
    }

    /// <summary>The slot's icon as the mod has it: its replacement (large one first), else the game's.</summary>
    Bitmap? GearIconImage(GearSlot s)
    {
        foreach (var (pkg, tex) in GearData.Textures(s).AsEnumerable().Reverse())
        {
            int view = Array.FindIndex(Applier.IconPackages, x => x.File.Equals(pkg, StringComparison.OrdinalIgnoreCase));
            string? src = view >= 0 ? draft.Textures[view].FirstOrDefault(r => r.Texture.Equals(tex, StringComparison.OrdinalIgnoreCase)).Source
                : draft.Extra.FirstOrDefault(x => x.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase) && x.Texture.Equals(tex, StringComparison.OrdinalIgnoreCase)).Source;
            if (src != null && PostWriter.Picture(src) is { } mine) return mine;
        }
        if (catalog == null) return null;
        foreach (var (pkg, tex) in GearData.Textures(s).AsEnumerable().Reverse())
            try
            {
                lock (Ui.StockLock)
                    if (catalog.Preview(pkg, tex) is { } p) return MhoPackageModifier.TextureDecode.ToBitmap(p.Bgra, p.W, p.H);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
        return null;
    }

    /// <summary>Game's Icon: the slot's icon replacements taken out (the Icons tab's tables too: Save reads them).</summary>
    void GearIconOff(GearSlot s)
    {
        RemoveIconReplacements(GearData.Textures(s));
        foreach (var tp in texturePages) tp.Refresh();
    }

    /// <summary>A slot's name in the draft (English); empty or the game's own takes the change off.</summary>
    void SetGearName(GearSlot s, string typed) => SetGearString(s, s.NameId, typed);

    /// <summary>One of a slot's strings (its name, or its second line) in the draft (English); empty or the game's own takes the change off.</summary>
    void SetGearString(GearSlot s, ulong id, string typed)
    {
        if (id == 0) return;
        string text = typed.Trim(), original = GearOriginalText(id);
        if (text == GearText(id)) return;
        draft.Strings.RemoveAll(x => x.Language == "eng" && x.Id == id);
        stringsPage.Drop("eng", id);
        if (text.Length > 0 && text != original)
        {
            var r = new StringReplacement("eng", CostumeMove.StringFileFor("eng", id), id, text);
            draft.Strings.Add(r);
            stringsPage.Put(r);
        }
        draft.Strings = [.. draft.Strings];   // (a new list: the unsaved-changes check sees it)
        // every row showing that string (a shared one shows on several)
        foreach (var row in gearRows.Where(x => x.Slot.NameId == id || x.Types.Any(t => t.Id == id)).ToList())
        {
            if (row.Name.Text != GearText(row.Slot.NameId) && row.Slot.NameId != 0) row.Name.Text = GearText(row.Slot.NameId);
            foreach (var (tid, box) in row.Types) if (box.Text != GearText(tid)) box.Text = GearText(tid);
            ShowGearRow(row);
        }
    }

    /// <summary>
    /// A picture (PNG, JPG, WebP, .dds …) as the slot's icons: converted to each of its textures' size and format (DXT5, alpha
    /// kept), put in as icon replacements (the small one in the classic icons, the large one in More Icon Packages). False when
    /// nothing was put in.
    /// </summary>
    bool PutGearIcon(GearSlot s, string image)
    {
        if (catalog == null) return false;
        var targets = GearData.Textures(s);
        if (targets.Count == 0) { Dialog.Show(this, "This slot has no icon in the game to replace.", "Icon Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        string png = image, stamp = Guid.NewGuid().ToString("N")[..8];
        string? temp = null;
        try
        {
            if (image.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                using var bmp = PostWriter.Picture(image) ?? throw new InvalidDataException("the .dds can't be read");
                temp = png = Path.Combine(Settings.Home, "converted", $"gear_{stamp}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(png)!);
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            }
            var made = new List<(string Package, string Texture, string Dds)>();
            foreach (var (pkg, tex) in targets)
            {
                string dds = Path.Combine(Settings.Home, "converted", $"gear_{ModInstaller.Sanitise(Path.GetFileNameWithoutExtension(pkg))}_{ModInstaller.Sanitise(tex)}_{stamp}.dds");
                Directory.CreateDirectory(Path.GetDirectoryName(dds)!);
                catalog.ImageToDds(pkg, tex, png, dds);
                made.Add((pkg, tex, dds));
            }
            RemoveIconReplacements(targets);
            foreach (var (pkg, tex, dds) in made)
            {
                int view = Array.FindIndex(Applier.IconPackages, x => x.File.Equals(pkg, StringComparison.OrdinalIgnoreCase));
                if (view >= 0) draft.Textures[view].Add((tex, dds));
                else draft.Extra.Add((pkg, tex, dds));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or System.Runtime.InteropServices.ExternalException)
        {
            Dialog.Show(this, $"{Path.GetFileName(image)} can't be used as the icon:" + Environment.NewLine + "  • " + ex.Message, "Icon Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally { if (temp != null && File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { } }
        foreach (var tp in texturePages) tp.Refresh();
        return true;
    }

    /// <summary>Make Icon From 3D View for a slot: the idle pose (paused, no effects), then the icon maker in gear mode.</summary>
    void MakeGearIcon(GearSlot s)
    {
        if (gearPreview?.View3D is not { } view || !view.Ready)
        {
            Dialog.Show(this, editing == null ? "Save the mod once: the 3D view shows its saved model." : "The 3D view isn't ready yet: try again once the character shows.", "No 3D View", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        OpenGearMaker(s, null, null);
    }

    /// <summary>Where an icon made here keeps its makings (the character layer and the look), per mod and texture.</summary>
    string GearMakings(GearSlot s) => Path.Combine(Settings.Home, "gear-icons",
        ModInstaller.Sanitise(string.IsNullOrWhiteSpace(nameBox.Text) ? editing?.FolderName ?? "New Mod" : nameBox.Text.Trim()), ModInstaller.Sanitise(s.Texture ?? s.Unique ?? "icon"));

    /// <summary>
    /// A click on a slot's icon (Kurt, 2026-11-17): the icon maker opens on it to add a glow, change colors and so on. An icon
    /// made here opens as it was made (its character layer and look); any other (the game's, or a picture put in) opens as the
    /// layer itself.
    /// </summary>
    /// <param name="from">Copy From Slot (Kurt, 2026-11-17): start from another slot's icon (a unique from its standard slot's):
    /// its character layer on transparency and its look as settings to change (outline color, sliders), not baked in; Use as
    /// Icon then writes this one's icon.</param>
    void ReopenGearIcon(GearSlot s, GearSlot? from = null)
    {
        if (GearData.Textures(s).Count == 0) return;
        var src = from ?? s;
        bool changed = GearData.Textures(src).Any(t => HasIconReplacement(t.Package, t.Texture));
        if (changed && PowerIconForm.LoadMakings(GearMakings(src)) is var (layer, look)) { OpenGearMaker(s, layer, look); return; }
        if (GearIconImage(src) is not { } img) return;
        // (a small picture: made 3× the large icon's size so the eraser and the zoom have pixels to work with)
        var big = new Bitmap(384, 384, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality; g.DrawImage(img, new Rectangle(0, 0, 384, 384)); }
        img.Dispose();
        OpenGearMaker(s, big, new PowerIconForm.Look(Color.FromArgb(22, 32, 58).ToArgb(), true, false, Color.FromArgb(4, 6, 14).ToArgb(), 1, 0, 1, 1, 1, 0, 0, 0, Color.FromArgb(10, 10, 12).ToArgb(), 0, true, 1, 0, 0));
    }

    void OpenGearMaker(GearSlot s, Bitmap? layer, PowerIconForm.Look? look)
    {
        if (GearData.Textures(s).Count == 0) { Dialog.Show(this, "This one has no icon in the game to replace.", "Icon Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var view = gearPreview?.View3D;
        var f = new PowerIconForm(size => view is { Ready: true } v ? v.IconSnapshot(size, 2) : null, () => view?.CurrentAnimation, $"{GearData.HeroName(s.Hero)}'s {GearName(s)}", gear: true, startLayer: layer, startLook: look);
        f.Use += png =>
        {
            if (!PutGearIcon(s, png)) return;
            try { f.SaveMakings(GearMakings(s)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
            ShowGearSlots();
        };
        f.FormClosed += (_, _) => { f.Dispose(); layer?.Dispose(); };
        LastGearMakerForTest = f;
        f.Show(FindForm());
        if (view != null) f.PlaceBeside(view); else f.Location = FindForm()?.Location ?? Point.Empty;
    }

    /// <summary>The 3D view on the idle pose, paused (Kurt: gear icons start from the idle), once its animations are in.</summary>
    async void GearIdle()
    {
        for (int i = 0; i < 300 && !IsDisposed && gearPreview != null; i++)
        {
            if (gearPreview.View3D is { Ready: true } v && v.AllAnimations.Count > 0)
            {
                var all = v.AllAnimations;
                string? idle = all.FirstOrDefault(a => a.Name.Equals("idle", StringComparison.OrdinalIgnoreCase))?.Name
                    ?? all.FirstOrDefault(a => a.Name.Contains("idle", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("combat", StringComparison.OrdinalIgnoreCase))?.Name;
                if (idle != null) gearPreview.PlayAnimation(idle, false);
                return;
            }
            await Task.Delay(100);
        }
    }
}

sealed partial class ModEditorView
{
    /// <summary>--gear-test: the Gear page on the mod's hero, slot 1 renamed and given <paramref name="image"/> as its icon
    /// (as the page's own controls do), or (<paramref name="image"/> null) both taken back; then saved. The checks are the caller's.</summary>
    internal static List<string> GearRowsForTest = [];
    internal static PowerIconForm? LastGearMakerForTest;
    internal static ulong GearTestTypeId, GearTestNameId;
    internal static bool GearSlotNameLocked;
    internal static string GearAllCheck = "";
    internal static string GearCopyCheck = "";
    internal async Task<(GearSlot? Slot, string? Saved)> GearForTest(string name, string? image)
    {
        ShowPage("Gear");
        for (int i = 0; i < 300 && gearRows.Count == 0; i++) await Task.Delay(100);
        if (gearRows.Count == 0) return (null, null);
        var slot = gearRows[0].Slot;
        // the name is renamed on the slot's first unique item (a slot's name is locked: Kurt, 2026-11-17)
        var uqName = gearRows.FirstOrDefault(r => r.Slot.Unique != null && r.Slot.NameId != 0 && !r.Name.ReadOnly).Slot;
        GearTestNameId = uqName?.NameId ?? 0;
        GearSlotNameLocked = gearRows[0].Name.ReadOnly;
        if (image != null)
        {
            if (uqName != null) SetGearName(uqName, name);
            // the second line of the slot's highest item version (Belt15 style: the list under the name)
            GearTestTypeId = PowerDbNow() is { } tdb && GearData.TierGroups(tdb, slot) is { Count: > 0 } tg ? tg[^1].TypeId : slot.TypeId;
            SetGearString(slot, GearTestTypeId, name + " Line");
            if (!PutGearIcon(slot, image)) return (slot, null);
        }
        else
        {
            if (uqName != null) SetGearName(uqName, "");
            SetGearString(slot, GearTestTypeId, "");
            GearIconOff(slot);
        }
        ShowGearSlots();
        GearRowsForTest = [.. gearRows.Select(r => r.Slot.Label)];
        // All Five Slots at Once: level 10's box (as typed and left), then every slot's level 10 line; then the box cleared again
        if (image != null && PowerDbNow() is { } adb && gearSlotsTable!.Controls.OfType<TableLayoutPanel>().FirstOrDefault() is { } allBox
            && allBox.Controls.OfType<TableLayoutPanel>().FirstOrDefault() is { } allBody && allBody.GetControlFromPosition(1, 11) is { } cell)
        {
            var tb = cell as TextBox ?? cell.Controls.OfType<TextBox>().First();
            var onLeave = typeof(Control).GetMethod("OnLeave", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            tb.Text = "Test All"; onLeave.Invoke(tb, [EventArgs.Empty]);
            var std = gearAll![gearHeroes[gearHeroBox!.SelectedIndex]].Where(x => x.Unique == null).ToList();
            var got = std.Select(x => GearText(GearData.Tiers(adb)[(x.Hero.ToLowerInvariant(), x.Number)][9].TypeId)).ToList();
            bool allOk = got.All(g => g.StartsWith("Test All ", StringComparison.Ordinal) && g.Length > 9);
            tb.Text = ""; onLeave.Invoke(tb, [EventArgs.Empty]);
            var back = std.Select(x => GearTextChanged(GearData.Tiers(adb)[(x.Hero.ToLowerInvariant(), x.Number)][9].TypeId)).ToList();
            GearAllCheck = $"{(allOk && back.All(b => !b) ? "PASS" : "FAIL")} All Five Slots at Once, level 10 \"Test All\": {string.Join(" / ", got)}; cleared: back to the game's {back.Count(b => !b)} of {back.Count}";
            // Fill All Slots: levels 2 and 3 typed (no Enter), then the button
            TextBox Box(int lvl) { var c = allBody.GetControlFromPosition(1, lvl + 1)!; return c as TextBox ?? c.Controls.OfType<TextBox>().First(); }
            // All Levels: Enter copies it into every level's box, writing nothing
            {
                var gc = allBody.GetControlFromPosition(1, 1)!;
                var g = gc as TextBox ?? gc.Controls.OfType<TextBox>().First();
                int Changed() => std.Sum(x => GearData.Tiers(adb)[(x.Hero.ToLowerInvariant(), x.Number)].Count(t => GearTextChanged(t.TypeId)));
                int before = Changed();
                var was = Enumerable.Range(1, GearData.Tiers(adb)[(std[0].Hero.ToLowerInvariant(), std[0].Number)].Count).Select(l => Box(l).Text).ToList();
                g.Text = "Every Level";
                typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(g, [new KeyEventArgs(Keys.Enter)]);
                int levels = GearData.Tiers(adb)[(std[0].Hero.ToLowerInvariant(), std[0].Number)].Count;
                bool copied = Enumerable.Range(1, levels).All(l => Box(l).Text == "Every Level");
                bool unwritten = Changed() == before;
                GearAllCheck += $"\n{(copied && unwritten ? "PASS" : "FAIL")} All Levels copies into all {levels} level boxes ({copied}) and writes nothing yet ({unwritten})";
                g.Text = "";
                for (int l = 1; l <= levels; l++) Box(l).Text = was[l - 1];   // (as they were: nothing was written)
            }
            Box(2).Text = "Fill Two"; Box(3).Text = "Fill Three";
            var fillBtn = allBody.Controls.OfType<Button>().First(b => b.AccessibleName == "Fill All Slots" || b.Text == "Fill All Slots");
            typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(fillBtn, [EventArgs.Empty]);
            var two = std.Select(x => GearText(GearData.Tiers(adb)[(x.Hero.ToLowerInvariant(), x.Number)][1].TypeId)).ToList();
            var three = std.Select(x => GearText(GearData.Tiers(adb)[(x.Hero.ToLowerInvariant(), x.Number)][2].TypeId)).ToList();
            bool fillOk = two.All(g => g.StartsWith("Fill Two ", StringComparison.Ordinal)) && three.All(g => g.StartsWith("Fill Three ", StringComparison.Ordinal));
            Box(2).Text = ""; Box(3).Text = "";
            onLeave.Invoke(Box(2), [EventArgs.Empty]); onLeave.Invoke(Box(3), [EventArgs.Empty]);
            GearAllCheck += $"\n{(fillOk ? "PASS" : "FAIL")} Fill All Slots writes every typed level: {string.Join(" / ", two)}; {string.Join(" / ", three)}";
        }
        // Copy From Slot: slot 1's makings (a red outline) opened for its first unique: the same layer and look, not baked
        if (image != null && gearRows.FirstOrDefault(r => r.Slot.Unique != null && r.Slot.Number == slot.Number).Slot is { } uq)
        {
            var look = new PowerIconForm.Look(Color.FromArgb(22, 32, 58).ToArgb(), true, false, Color.Black.ToArgb(), 1, 0, 1, 1, 1, 0, 0.5f, 2, Color.Red.ToArgb(), 0, true, 1, 0, 0);
            using (var lay = new Bitmap(384, 384, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(lay)) g.FillEllipse(Brushes.Gold, 100, 100, 184, 184);
                string stem = GearMakings(slot);
                Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
                lay.Save(stem + ".png", System.Drawing.Imaging.ImageFormat.Png);
                File.WriteAllText(stem + ".json", System.Text.Json.JsonSerializer.Serialize(look));
            }
            bool hasButton = gearSlotsTable!.Controls.OfType<FlowLayoutPanel>().SelectMany(c => c.Controls.OfType<FlowLayoutPanel>()).SelectMany(b => b.Controls.OfType<Button>()).Any(b => b.AccessibleName == $"Copy From Slot {slot.Number}" || b.Text == $"Copy From Slot {slot.Number}");
            LastGearMakerForTest = null;
            ReopenGearIcon(uq, slot);
            await Task.Delay(800);
            var f = LastGearMakerForTest;
            bool sameLook = f != null && f.LookForTest == look;
            bool layerOk = f?.LayerForTest is { } fl && fl.Width == 384 && fl.GetPixel(192, 192).A == 255 && fl.GetPixel(10, 10).A == 0;
            GearCopyCheck = $"{(hasButton && sameLook && layerOk ? "PASS" : "FAIL")} Copy From Slot {slot.Number} on {uq.Unique}: button {hasButton}, the slot's look (red outline 2 px, glow 50 %) {sameLook}, its layer on transparency {layerOk}";
            f?.Close();
        }
        // (MHO_GEAR_SNAP=<png>: the page scrolled to the unique items, before the save)
        if (image != null && Environment.GetEnvironmentVariable("MHO_GEAR_SNAP") is { Length: > 0 } snap && gearSlotsTable?.Parent is ScrollableControl sc && FindForm() is Form form)
        {
            ShowGearSlots();   // (the boxes as the mod has them, after the checks above)
            await Task.Delay(2500);
            // All Five Slots at Once opened (the picture shows All Levels and the 15 levels), at the top
            foreach (var t in gearSlotsTable.Controls.OfType<TableLayoutPanel>().First().Controls.OfType<TableLayoutPanel>()) t.Visible = true;
            await Task.Delay(500);
            sc.AutoScrollPosition = new Point(0, 0);
            await Task.Delay(500);
            using var b = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(b, new Rectangle(0, 0, form.Width, form.Height));
            b.Save(snap);
        }
        return (slot, await SaveForTest());
    }
}
