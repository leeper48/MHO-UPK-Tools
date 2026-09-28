namespace MhoPackageModifier.Gui;

/// <summary>The Start tab: what the tool does, the safety rules in short, and one card per task that jumps to its tab.</summary>
sealed partial class MainForm
{
    readonly Label startStatus = new() { AutoSize = true, MaximumSize = new Size(1150, 0), Margin = new Padding(3, 2, 3, 6) };
    readonly CheckBox showStart = new() { Text = "Show This Page When the App Starts", AutoSize = true };

    TabPage BuildStartTab()
    {
        var cards = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        (string Title, string Text, Func<TabPage> Page)[] tasks =
        [
            ("Change a Value", "Fog, sun and sky colours, material parameters, any number or colour in an object. Browse to the object, then edit it on the Properties tab; the same change can go to every package that has the object.", () => browsePage),
            ("Add, Move or Reshape Buildings in a Zone", "Export a zone's placed meshes to Blender, duplicate, move, scale or edit them, and bring the changes back (Hightown, Odin's Palace).", () => placementsPage),
            ("Replace One Mesh with My Own Model", "Export a single StaticMesh to FBX, edit it, and import it back into its package.", () => meshesPage),
            ("Replace or Add a Texture", "See every texture as you click it; replace one with your own PNG, JPG, BMP or DDS, or add a new one.", () => texturesPage),
            ("Rebuild a Zone's Distant View", "One click rebuilds a zone's main level with its whole recipe: sky, distant buildings or LODs, ground, water. Also: export placed meshes to bake LODs.", () => zonesPage),
            ("Copy a Material or Object Between Packages", "Copy an object with everything it needs, rename it, swap its textures; remove placed meshes; find material instances.", () => objectsPage),
            ("Undo, Redo or Restore Originals", "Every write can be undone step by step, or the package restored from its original .bak.", () => backupsPage),
            ("Run Any Command", "Every command-line tool, with its syntax and a filled-in example, including diagnostics.", () => toolsPage),
        ];
        int n = 0;
        foreach (var (title, text, page) in tasks)
        {
            var card = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(4, 4, 12, 8), Padding = new Padding(8), Tag = "card" };
            var b = Btn(title + "  →", () => tabs.SelectedTab = page());
            b.Font = new Font("Segoe UI Semibold", 11f);
            b.Tag = "cardtitle";
            b.Dock = DockStyle.Top;
            card.Controls.Add(b, 0, 0);
            card.Controls.Add(Hint(text, 540), 0, 1);
            cards.Controls.Add(card, n % 2, n / 2);
            n++;
        }

        var safety = new FieldGrid();
        safety.Full(Hint(
            "• Close the game before writing. The header shows whether it is running; writes are blocked while it is.\n" +
            "• Dry run first. \"Check (Dry Run)\" builds and verifies the result without touching the game; the file goes to import_out next to the app.\n" +
            "• The original is kept. The first write to a package copies it to <package>.upk.bak, and that copy is never changed or deleted.\n" +
            "• Every write is verified: the new package is built in a temporary file and read back before it replaces the game file.\n" +
            "• Undo steps back one write at a time (Backups tab, Ctrl+Z). \"Revert Selected to Original\" copies the .bak back.\n" +
            "• Your work files go in the export folder (Meshes tab). The app never deletes or overwrites your files there; re-exports get new names.\n" +
            "• Press F1 on any tab for its section of the manual."));

        var status = new FieldGrid();
        status.Full(startStatus);
        showStart.CheckedChanged += (_, _) => settings.ShowStartOnLaunch = showStart.Checked;
        status.Full(NoWrap(Btn("Open the Manual", () => ShowHelp("start")), Btn("Refresh Status", RefreshStart), showStart));
        status.Full(NoWrap(Btn("Check for Updates", () => _ = CheckForUpdates(quiet: false)), checkUpdates,
            new Label { Text = $"   You Have v{Updater.Current}.", AutoSize = true, Padding = new Padding(0, 8, 0, 0), Tag = "hint" }));

        return Stacked("Start",
            Heading("MHO Package Modifier"),
            Hint("Reads and edits Marvel Heroes Omega's game packages (.upk): meshes, textures, materials, placed buildings and whole zones. What do you want to do?"),
            cards,
            Section("Safety Rules", safety),
            Section("Status", status));
    }

    void RefreshStart()
    {
        showStart.Checked = settings.ShowStartOnLaunch;
        if (!Directory.Exists(gameFolder.Text)) { startStatus.Text = "Game Folder Not Found. Set It at the Top (the CookedPCConsole Folder of the Game)."; return; }
        int packages = 0, baks = 0, modified = 0;
        foreach (string f in Directory.EnumerateFiles(gameFolder.Text))
        {
            if (f.EndsWith(".upk.bak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap.bak", StringComparison.OrdinalIgnoreCase))
            {
                baks++;
                string live = f[..^4];
                if (File.Exists(live) && new FileInfo(live).Length != new FileInfo(f).Length) modified++;
            }
            else if (f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) packages++;
        }
        startStatus.Text = Look.TitleCase($"Game folder: {gameFolder.Text}\n{packages:N0} packages; {baks} have an original kept as .bak ({modified} differ in size from it: see the Backups tab).\n" +
                           $"Export folder (your work files): {exportFolder.Text}");
    }
}
