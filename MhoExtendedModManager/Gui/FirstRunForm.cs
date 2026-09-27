using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// First run (no game folder or no library yet): 1. the Marvel Heroes folder, found from Steam or picked;
/// 2. "Were you using MHModManager?": migrate its mods, or start with an empty library. The library goes to its
/// automatic place; nothing is written to the game folder here.
/// </summary>
sealed class FirstRunForm : Form
{
    readonly Settings settings;
    readonly ComboBox gameBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    readonly RadioButton fresh = new() { Text = "No, start with an empty mod library", AutoSize = true, Checked = true };
    readonly RadioButton migrate = new() { Text = "Yes, bring its mods, their order and its backups over (its folder is left as it is):", AutoSize = true };
    readonly ComboBox oldBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, Enabled = false };
    readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Visible = false };
    readonly Button ok = new() { Text = "Set Up", AutoSize = true };
    bool finished;

    public FirstRunForm(Settings settings)
    {
        this.settings = settings;
        Text = "MHO Extended Mod Manager: First-Run Setup";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 900; Height = 640; StartPosition = FormStartPosition.CenterScreen;

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        int row = 0;
        void Row(Control c, Control? right = null) { t.Controls.Add(c, 0, row); if (right != null) t.Controls.Add(right, 1, row); else t.SetColumnSpan(c, 2); row++; }
        Label L(string s, bool bold = false) => new() { Text = s, AutoSize = true, MaximumSize = new Size(840, 0), Padding = new Padding(0, bold ? 10 : 2, 0, 4), Font = bold ? new Font(Font, FontStyle.Bold) : Font };

        Row(L("1. Where is Marvel Heroes installed?", true));
        Row(L("Found from Steam where possible. It's the folder that holds UnrealEngine3 and Data."));
        var browseGame = new Button { Text = "Browse…", AutoSize = true };
        browseGame.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "The Marvel Heroes folder (holds UnrealEngine3 and Data)", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) gameBox.Text = d.SelectedPath;
        };
        Row(gameBox, browseGame);

        Row(L("2. Were you using MHModManager (the earlier mod manager)?", true));
        Row(fresh);
        Row(migrate);
        var browseOld = new Button { Text = "Browse…", AutoSize = true, Enabled = false };
        browseOld.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { Description = "MHModManager's folder (holds MHModManager.exe and data)", UseDescriptionForTitle = true };
            if (d.ShowDialog(this) == DialogResult.OK) oldBox.Text = d.SelectedPath;
        };
        migrate.CheckedChanged += (_, _) => { oldBox.Enabled = browseOld.Enabled = migrate.Checked; };
        Row(oldBox, browseOld);
        Row(L("Your mods are kept in " + settings.LibraryPath + " (Settings… → Move library can put them elsewhere later). Nothing in the game folder changes until you press Apply."));
        t.RowStyles.Clear();
        for (int i = 0; i < row; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(log, 0, row); t.SetColumnSpan(log, 2); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); row++;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        t.Controls.Add(buttons, 0, row); t.SetColumnSpan(buttons, 2); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(t);
        CancelButton = cancel;
        ok.Click += (_, _) => { if (settings.IsSetUp && finished) { DialogResult = DialogResult.OK; Close(); } else Finish(); };

        Load += (_, _) =>
        {
            Theme.Apply(this, Palette.Dark);
            foreach (string g in Settings.FindGame()) gameBox.Items.Add(g);
            if (settings.GameRoot != null && Settings.IsGameRoot(settings.GameRoot)) gameBox.Text = settings.GameRoot;
            else if (gameBox.Items.Count > 0) gameBox.SelectedIndex = 0;
        };
        Shown += async (_, _) =>
        {
            // Look for an old manager in the usual places (a few seconds at most) and suggest it.
            var old = await Task.Run(() => Settings.FindOldManager(TimeSpan.FromSeconds(4)));
            foreach (string o in old) oldBox.Items.Add(o);
            if (old.Count > 0) { oldBox.SelectedIndex = 0; migrate.Checked = true; }
        };
    }

    async void Finish()
    {
        string game = gameBox.Text.Trim();
        if (!Settings.IsGameRoot(game)) { Dialog.Show(this, "That folder has no UnrealEngine3\\MarvelGame\\CookedPCConsole. Pick the Marvel Heroes folder.", "Not the Game Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (migrate.Checked && Settings.LibraryData(oldBox.Text.Trim()) == null) { Dialog.Show(this, "That isn't MHModManager's folder (expected data\\mods inside it).", "Not MHModManager's Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        settings.GameRoot = game;
        ok.Enabled = false; UseWaitCursor = true; log.Visible = true;
        string library = settings.LibraryPath, old = oldBox.Text.Trim();
        bool doMigrate = migrate.Checked;
        var s = settings;
        string output = await Task.Run(() =>
        {
            var sw = new StringWriter();
            var prev = Console.Out; Console.SetOut(sw);
            try
            {
                if (doMigrate) Migration.Run(old, library, s);
                else { ModInstaller.CreateEmptyLibrary(library); Console.WriteLine($"Created an empty mod library in {library}."); }
                s.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.WriteLine("Stopped: " + ex.Message); }
            finally { Console.SetOut(prev); }
            return sw.ToString();
        });
        UseWaitCursor = false;
        log.Text = output.Replace("\r\n", "\n").Replace("\n", "\r\n");
        finished = settings.IsSetUp;
        if (finished) ok.Text = "Open the Mod List";
        ok.Enabled = true;
    }
}
