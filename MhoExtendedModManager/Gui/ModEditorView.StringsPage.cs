using System.Text.RegularExpressions;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The Editor's Strings tab.</summary>
sealed partial class ModEditorView
{
    /// <summary>String replacements: search the game's original text (per language), add rows, type the new text.</summary>
    sealed class StringsPage : UserControl
    {
        readonly ModEditorView f;
        readonly DropDown lang = new() { Width = 90 };
        readonly TextBox search = new() { Width = 420 };
        readonly DataGridView results;
        readonly DataGridView grid;
        readonly Label found = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0), Tag = "subtle" };
        // Columns are found by these names, not by their (Title Case) headers: 0.21.1 changed the replacement column's
        // header and a lookup by the old text made it read-only (Kurt couldn't type replacements until 0.22.3).
        const string ReplacementCol = "replacement", UsedByCol = "usedby";
        // What each game string is attached to (hero name, NPC, item, power …), from the game's data (StringUsage).
        StringUsage? usage;

        public StringsPage(ModEditorView f)
        {
            this.f = f; Dock = DockStyle.Fill;
            float s = f.S;
            search.Width = (int)(420 * s); lang.Width = (int)(90 * s);
            results = Ui.Grid(s, false, ("ID", 190), ("Original Text", 0), ("Used By", 420));
            results.Columns[^1].Name = UsedByCol;
            results.Tag = "keepselection";
            grid = Ui.Grid(s, false, ("Lang", 60), ("File", 250), ("ID", 190), ("Original", 0), ("Replacement (Type Here)", 0), ("Used By", 360));
            grid.Columns[4].Name = ReplacementCol; grid.Columns[5].Name = UsedByCol;
            grid.ReadOnly = false;
            foreach (DataGridViewColumn c in grid.Columns) c.ReadOnly = c.Name != ReplacementCol;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            Ui.StyleGrid(grid);
            foreach (var st in f.draft.Strings) grid.Rows[grid.Rows.Add(st.Language, st.File, st.Id.ToString(), "", st.Text)].Tag = st;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
            bar.Controls.AddRange([new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 8, 4, 0), Tag = "subtle" }, lang,
                new Label { Text = "Find Text or ID", AutoSize = true, Padding = new Padding(12, 8, 4, 0), Tag = "subtle" }, search, Ui.AccentButton("Search", Search, tip: "Search the game's original text of the chosen language (text or string ID)."), found]);
            search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            MhoPackageModifier.Gui.SearchBox.AddClear(search);
            var split = new GradientSplit { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
            split.Panel1.Controls.Add(results);
            split.Panel1.Controls.Add(Toolbar(Ui.FlatButton("Add Selected to the Mod  ↓", AddSelected, tip: "Add the selected game strings to the mod, to type their new text below."), new Label { Text = "GAME TEXT", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(12, 8, 0, 0) }));
            split.Panel2.Controls.Add(grid);
            split.Panel2.Controls.Add(Toolbar(Ui.Tip(Ui.FlatButton("Remove Selected Rows", () => { foreach (DataGridViewRow r in grid.SelectedRows) grid.Rows.Remove(r); }), "Take the selected string changes out of the mod."),
                Ui.FlatButton("Import Changes (.JSON)", ImportJson, tip: "Load string changes from a .JSON in the mod format (e.g. one saved from Extract)."), new Label { Text = "THIS MOD'S CHANGES", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(12, 8, 0, 0) }));
            Controls.Add(split); Controls.Add(bar);
            results.CellDoubleClick += (_, _) => AddSelected();
            VisibleChanged += (_, _) =>
            {
                if (!Visible || lang.Items.Count > 0 || f.catalog == null) return;
                foreach (string l in f.catalog.Languages()) lang.Items.Add(l);
                lang.SelectedItem = lang.Items.Contains("eng") ? "eng" : lang.Items.Count > 0 ? lang.Items[0] : null;
                FillOriginals();
                LoadUsage();
            };
        }

        public string Check()
        {
            bool editable = !grid.Columns[ReplacementCol].ReadOnly && grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Name != ReplacementCol).All(c => c.ReadOnly);
            int used = results.Rows.Cast<DataGridViewRow>().Count(r => (r.Cells[UsedByCol].Value as string ?? "").Length > 0);
            string first = results.Rows.Count > 0 ? $"{results.Rows[0].Cells["Original Text"].Value} | {results.Rows[0].Cells[UsedByCol].Value}" : "";
            return $"replacement editable: {editable}; results {results.Rows.Count}, with Used By {used}; first: {first}";
        }

        public void SearchForTest(string text) { search.Text = text; Search(); }

        Task<StringUsage?>? usageTask;

        /// <summary>Starts building the index (once); Search waits for it, so hero names can be put first.</summary>
        Task<StringUsage?> UsageTask()
        {
            if (usageTask != null) return usageTask;
            string? root = f.game?.Root;
            return usageTask = root == null ? Task.FromResult<StringUsage?>(null) : Task.Run(() => StringUsage.Load(root));
        }

        async void LoadUsage()
        {
            usage = await UsageTask();
            FillUsage(results); FillUsage(grid);
        }

        /// <summary>The Used By column: the first use in plain words (+N more); every use in the cell's tooltip.</summary>
        void FillUsage(DataGridView g)
        {
            if (usage == null) return;
            foreach (DataGridViewRow r in g.Rows)
            {
                ulong id = r.Tag is (string, string, ulong i, string) ? i : ulong.TryParse(r.Cells["ID"].Value as string, out ulong j) ? j : 0;
                var uses = usage.For(id);
                var cell = r.Cells[UsedByCol];
                cell.Value = uses.Count == 0 ? "(not used by the game's data)" : StringUsage.Describe(uses[0]) + (uses.Count > 1 ? $"  (+{uses.Count - 1} more)" : "");
                cell.ToolTipText = uses.Count == 0 ? "" : string.Join("\n", uses.Take(25).Select(StringUsage.Describe)) + (uses.Count > 25 ? $"\n… {uses.Count - 25} more" : "");
                cell.Style.ForeColor = uses.Count > 0 && StringUsage.Rank(uses) == 0 ? Ui.TagCharacter : Ui.Subtle;
            }
        }

        async void Search()
        {
            if (f.catalog == null || lang.SelectedItem is not string l) return;
            string q = search.Text.Trim();
            if (q.Length < 2) return;
            found.Text = "Searching…";
            var u = usage ??= await UsageTask();
            var hits = await Task.Run(() =>
            {
                var all = f.catalog.Strings(l).Where(s => s.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Id.ToString() == q);
                // Hero names first, then costumes, team-ups, powers, NPCs, the rest (exact matches before partial ones).
                if (u != null) all = all.OrderBy(s => s.Text.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(s => StringUsage.Rank(u.For(s.Id)));
                return all.Take(501).ToList();
            });
            results.Rows.Clear();
            foreach (var h in hits.Take(500)) results.Rows[results.Rows.Add(h.Id.ToString(), h.Text)].Tag = (l, h.File, h.Id, h.Text);
            FillUsage(results);
            results.ClearSelection();
            found.Text = hits.Count > 500 ? "500+ found (showing 500; search more precisely)" : $"{hits.Count} found  ·  double-click or select and Add";
        }

        void AddSelected()
        {
            foreach (DataGridViewRow r in results.SelectedRows)
            {
                if (r.Tag is not (string l, string file, ulong id, string text)) continue;
                if (grid.Rows.Cast<DataGridViewRow>().Any(x => (string)x.Cells["ID"].Value! == id.ToString() && (string)x.Cells["Lang"].Value! == l)) continue;
                grid.Rows.Add(l, file, id.ToString(), text, text);
            }
            FillUsage(grid);
        }

        /// <summary>Shows each existing row's original text (for comparison).</summary>
        async void FillOriginals()
        {
            if (f.catalog == null) return;
            foreach (string l in grid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells["Lang"].Value!).Distinct().ToList())
            {
                var byId = (await Task.Run(() => f.catalog.Strings(l))).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Text);
                foreach (DataGridViewRow r in grid.Rows)
                    if ((string)r.Cells["Lang"].Value! == l && ulong.TryParse((string)r.Cells["ID"].Value!, out ulong id) && byId.TryGetValue(id, out string? t)) { r.Cells["Original"].Value = t; r.Cells["Original"].Style.ForeColor = Ui.Subtle; }
            }
        }

        /// <summary>A &lt;lang&gt;.json in MHModManager's format (e.g. from another mod or an extract).</summary>
        void ImportJson()
        {
            using var d = new OpenFileDialog { Title = "Import String Changes", Filter = "JSON (*.json)|*.json" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string l = Path.GetFileNameWithoutExtension(d.FileName);
            if (l.Length != 3) l = lang.SelectedItem as string ?? "eng";
            int n = 0;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(d.FileName));
                foreach (var file in doc.RootElement.EnumerateObject())
                    foreach (var e in file.Value.EnumerateObject())
                        if (ulong.TryParse(e.Name, out _) && e.Value.TryGetProperty("String", out var s))
                        {
                            foreach (var dup in grid.Rows.Cast<DataGridViewRow>().Where(r => (string)r.Cells["ID"].Value! == e.Name && (string)r.Cells["Lang"].Value! == l).ToList()) grid.Rows.Remove(dup);
                            grid.Rows.Add(l, file.Name, e.Name, "", s.GetString() ?? ""); n++;
                        }
            }
            catch (System.Text.Json.JsonException ex) { Dialog.Show(this, "Not a string file: " + ex.Message, "Import"); return; }
            FillOriginals();
            Dialog.Show(this, $"Imported {n} string(s) as language '{l}'.", "Import");
        }

        public List<StringReplacement> Collect()
        {
            grid.EndEdit();
            var list = new List<StringReplacement>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                string l = (string)r.Cells["Lang"].Value!, file = (string)r.Cells["File"].Value!, text = r.Cells[ReplacementCol].Value as string ?? "";
                ulong id = ulong.Parse((string)r.Cells["ID"].Value!);
                // Keep an edited string's variants and flags as they were in the mod.
                var old = r.Tag as StringReplacement;
                list.Add(new StringReplacement(l, file, id, text, old?.FlagsProduced ?? 0, old?.Variants));
            }
            return list;
        }
    }
}
