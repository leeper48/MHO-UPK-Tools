namespace MhoPackageModifier.Gui;

/// <summary>
/// The Tools tab: every command-line command (CommandCatalog), grouped, with its syntax, what it does, and an example
/// filled in from the open package, the selected export and the folders. Runs through the same entry point as the CLI;
/// commands that write are held back while the game runs and get --dry-run unless the box is cleared.
/// </summary>
sealed partial class MainForm
{
    readonly TreeView toolList = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, FullRowSelect = true, ItemHeight = 26, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    static readonly Font regularTreeFont = new("Segoe UI", 9f);
    readonly Label toolSyntax = new() { AutoSize = true, MaximumSize = new Size(760, 0), Font = new Font(FontFamily.GenericMonospace, 9f), Margin = new Padding(3, 4, 3, 4) };
    readonly Label toolSummary = new() { AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(3, 4, 3, 8) };
    readonly TextBox toolArgs = new() { Multiline = true, Height = 64, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 9f) };
    readonly CheckBox toolDry = new() { Text = "Dry Run (Check Only)", AutoSize = true, Checked = true };

    TabPage BuildToolsTab()
    {
        foreach (string grp in CommandCatalog.Groups)
        {
            // The tree's font is the bold one and commands use the regular one: a node font larger than the tree's gets clipped.
            var node = new TreeNode(grp);
            foreach (var c in CommandCatalog.All.Where(c => c.Group == grp))
                node.Nodes.Add(new TreeNode((c.Flag == "(scan)" ? "(Folder Scan)" : c.Flag) + (c.Writes ? "   (Writes)" : "")) { Tag = c, ToolTipText = c.Summary, NodeFont = regularTreeFont });
            toolList.Nodes.Add(node);
        }
        toolList.ExpandAll();
        toolList.ShowNodeToolTips = true;
        toolList.AfterSelect += (_, _) => ShowTool();
        Load += (_, _) => { toolList.SelectedNode = toolList.Nodes[0].Nodes[0]; toolList.Nodes[0].EnsureVisible(); };

        var g = new FieldGrid();
        g.Full(Hint("Every command of the tool. Pick one: its syntax and an example appear, filled in with the open package, the export selected on the Browse " +
                    "tab and the export folder. Edit the arguments and run; output goes to the log below. Words in <angle brackets> are yours to fill in.", 760));
        g.Row("Syntax:", toolSyntax);
        g.Row("What It Does:", toolSummary);
        g.Row("Arguments:", toolArgs);
        g.Full(NoWrap(toolDry, Hint("the game file is untouched", 600)));
        g.Full(NoWrap(
            Btn("Run", RunTool),
            Btn("Refill Example", ShowTool),
            Btn("Copy as Command Line", () => { if (toolArgs.Text.Trim().Length > 0) { Clipboard.SetText($"\"{Path.Combine(AppContext.BaseDirectory, "MHO_UPK_Mod.exe")}\" {ToolLine()}"); Log("Copied to the clipboard."); } })));
        var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        right.Controls.Add(g);

        var split = new GradientSplit { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(toolList);
        split.Panel2.Controls.Add(right);
        var page = new TabPage("Tools");
        page.Controls.Add(split);
        Shown += (_, _) => { if (split.Width > 600) split.SplitterDistance = (int)(split.Width * 0.28); };
        return page;
    }

    CommandCatalog.Command? SelectedTool() => toolList.SelectedNode?.Tag as CommandCatalog.Command;

    void ShowTool()
    {
        if (SelectedTool() is not { } c) return;
        toolSyntax.Text = (c.Flag == "(scan)" ? "" : c.Flag + " ") + c.Syntax;
        toolSummary.Text = c.Summary + (c.Writes ? "\nWrites to the game file (unless dry run): the original is kept as .bak and Undo steps back." : "\nRead-only.");
        string export = package != null && SelectedExport() is int i ? package.PathOf(package.Exports[i]) : "<export>";
        toolArgs.Text = c.Template
            .Replace("{pkg}", packagePath.Length > 0 ? Quote(packagePath) : "<package.upk>")
            .Replace("{folder}", Quote(gameFolder.Text))
            .Replace("{export}", Quote(export))
            .Replace("{out}", exportFolder.Text)
            .Replace(" --dry-run", "");
        toolDry.Parent!.Visible = c.Writes;
    }

    string ToolLine() => toolArgs.Text.Replace("\r", " ").Replace("\n", " ").Trim() + (SelectedTool() is { Writes: true } && toolDry.Checked && !toolArgs.Text.Contains("--dry-run") ? " --dry-run" : "");

    void RunTool()
    {
        if (SelectedTool() is not { } c) { Log("Pick a command in the list."); return; }
        string[] args = SplitArgs(ToolLine());
        if (args.Length == 0) return;
        if (args.Any(a => a.StartsWith('<') && a.EndsWith('>'))) { Log("Fill in the <placeholders> first."); return; }
        bool writes = c.Writes && !args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        if (writes && !Confirm($"This writes to the game file(s):\n\n{string.Join(' ', args.Select(Quote))}\n\nThe original is kept as .bak; Undo on the Backups tab steps back. Continue?")) return;
        RunCommand(c.Flag == "(scan)" ? "Folder scan" : c.Flag, args, writes, after: () => { if (writes) { RefreshBackups(); ReopenPackage(); } });
    }
}
