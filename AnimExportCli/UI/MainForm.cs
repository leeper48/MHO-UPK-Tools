using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using AnimExportCli.Packages;

namespace AnimExportCli.UI;

/// <summary>
/// The GUI, built entirely in code rather than a .resx-backed designer file —
/// there's no complex layout here that a designer earns its keep on, and a
/// plain C# file is easier to read and change than a generated one.
/// </summary>
public sealed class MainForm : Form
{
    private readonly Button _openButton = new() { Text = "Open Package...", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Label _packagePathLabel = new() { Text = "No package open.", AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    private readonly ListBox _meshList = new() { IntegralHeight = false };
    private readonly ComboBox _animSetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox _animNamesList = new() { IntegralHeight = false, SelectionMode = SelectionMode.None };
    private readonly TextBox _filterBox = new();
    private readonly TextBox _outputBox = new();
    private readonly Button _browseOutputButton = new() { Text = "Browse...", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _exportButton = new() { Text = "Export", Enabled = false, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8), Font = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold) };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        BackColor = Color.Black,
        ForeColor = Color.Gainsboro,
    };

    private Package? _package;
    private string? _packagePath;
    private IReadOnlyList<ExportWorkflow.MeshEntry> _meshes = [];
    private IReadOnlyList<ExportWorkflow.AnimSetEntry> _animSets = [];
    private bool _outputEditedByUser;
    private bool _suppressOutputTracking;

    /// <summary>Wraps one combo box choice — either "export every matching AnimSet" (Entry is null) or one specific AnimSet.</summary>
    private sealed class AnimSetChoice(string label, ExportWorkflow.AnimSetEntry? entry)
    {
        public ExportWorkflow.AnimSetEntry? Entry { get; } = entry;
        public override string ToString() => label;
    }

    /// <summary>
    /// A left-panel section label. AutoSize, not a guessed fixed height — the
    /// earlier fixed-height version is exactly what caused text to render cut
    /// off on a display where the real font metrics turned out taller than
    /// assumed. These labels are short enough now (see the shortening below)
    /// that the original horizontal-overflow risk AutoSize=false was guarding
    /// against doesn't apply any more either.
    /// </summary>
    private static Label MakeLeftLabel(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top };

    public MainForm()
    {
        Text = $"MHO Animation Exporter v{Program.Version}";
        // ApplicationIcon in the csproj covers the exe file's own icon
        // (Explorer, taskbar); this is the separate window/title-bar icon,
        // which doesn't automatically inherit it. Falls back to no icon
        // rather than throwing if the file's missing, e.g. a build where
        // AppIcon.ico didn't get copied next to the exe for some reason.
        string iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        MinimumSize = new Size(900, 600);
        Size = new Size(1050, 680);
        StartPosition = FormStartPosition.CenterScreen;
        // Explicitly no auto-scaling: this is a hand-built layout with no
        // designer-captured baseline for WinForms to scale pixel sizes
        // against, and the last attempt at DPI-aware scaling here made
        // things worse, not better (a button's text went from fully visible
        // to truncated). SetHighDpiMode in Program.cs already gets the
        // window rendered at the right physical size; this just stops
        // WinForms from trying to additionally rescale on top of that.
        AutoScaleMode = AutoScaleMode.None;

        var topLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(6),
        };
        topLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _packagePathLabel.Margin = new Padding(3, 8, 3, 3);
        topLayout.Controls.Add(_openButton, 0, 0);
        topLayout.Controls.Add(_packagePathLabel, 1, 0);

        // SplitterDistance deliberately isn't set here: the control has no
        // real width yet at construction time, so a value like 360 gets
        // silently clamped down to whatever tiny default width it starts
        // with, and never recovers once the form is actually sized. It's set
        // explicitly, after this container has a parent and a real size,
        // further down.
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new Padding(8) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        left.Controls.Add(MakeLeftLabel("Meshes:"), 0, 0);
        _meshList.Dock = DockStyle.Fill;
        _meshList.Margin = new Padding(0, 2, 0, 10);
        left.Controls.Add(_meshList, 0, 1);

        left.Controls.Add(MakeLeftLabel("Animation set:"), 0, 2);
        _animSetCombo.Dock = DockStyle.Top;
        _animSetCombo.Margin = new Padding(0, 2, 0, 10);
        left.Controls.Add(_animSetCombo, 0, 3);

        left.Controls.Add(MakeLeftLabel("Animations in this set:"), 0, 4);
        _animNamesList.Dock = DockStyle.Fill;
        _animNamesList.Margin = new Padding(0, 2, 0, 10);
        left.Controls.Add(_animNamesList, 0, 5);

        left.Controls.Add(MakeLeftLabel("Filter by name (optional):"), 0, 6);
        _filterBox.Dock = DockStyle.Top;
        _filterBox.Margin = new Padding(0, 2, 0, 10);
        left.Controls.Add(_filterBox, 0, 7);

        left.Controls.Add(MakeLeftLabel("Output folder:"), 0, 8);
        var outputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0, 2, 0, 14),
        };
        outputRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _outputBox.Dock = DockStyle.Top;
        _outputBox.Margin = new Padding(3, 6, 3, 3);
        outputRow.Controls.Add(_outputBox, 0, 0);
        outputRow.Controls.Add(_browseOutputButton, 1, 0);
        left.Controls.Add(outputRow, 0, 9);

        left.Controls.Add(_exportButton, 0, 10);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 8, 8, 8) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(new Label { Text = "Log:", AutoSize = true, Dock = DockStyle.Top }, 0, 0);
        right.Controls.Add(_log, 0, 1);

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        // A root TableLayoutPanel instead of adding topPanel and split
        // straight to the form's own Controls with Dock alone: that raw
        // Dock-order approach is exactly what caused the last two rounds of
        // overlap (I had the add-order rule backwards, then wasn't confident
        // enough in which way it actually goes to trust it a third time).
        // Explicit row placement has no such ambiguity.
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(topLayout, 0, 0);
        root.Controls.Add(split, 0, 1);
        Controls.Add(root);

        split.SplitterDistance = 360;

        var toolTip = new ToolTip();
        toolTip.SetToolTip(_meshList, "Includes non-character props (weapons, vehicles, etc.) alongside the character mesh.");
        toolTip.SetToolTip(_animNamesList, "Not selectable — for reference, to help pick a filter term below.");

        _openButton.Click += OnOpenClicked;
        _meshList.SelectedIndexChanged += OnMeshSelected;
        _animSetCombo.SelectedIndexChanged += OnAnimSetSelected;
        _browseOutputButton.Click += OnBrowseOutputClicked;
        _outputBox.TextChanged += (_, _) => { if (!_suppressOutputTracking) _outputEditedByUser = true; };
        _exportButton.Click += OnExportClicked;

        Log($"AnimExportCli v{Program.Version}");
        Log("Open a UPK to begin.");
    }

    private void OnOpenClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "Unreal package files (*.upk)|*.upk|All files (*.*)|*.*", Title = "Open a UPK" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        LoadPackage(dialog.FileName);
    }

    private void LoadPackage(string path)
    {
        Package package;
        try
        {
            package = Package.Open(path);
        }
        catch (InvalidPackageException ex)
        {
            MessageBox.Show(this, $"Could not read '{path}' as a package:\n\n{ex.Message}", "Open failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _package = package;
        _packagePath = path;
        _packagePathLabel.Text = path;

        _meshes = ExportWorkflow.ListMeshes(package);
        _meshList.Items.Clear();
        foreach (ExportWorkflow.MeshEntry mesh in _meshes) _meshList.Items.Add(mesh);

        _animSets = ExportWorkflow.ListAnimSets(package);
        _animSetCombo.Items.Clear();
        _animSetCombo.Items.Add(new AnimSetChoice("(all — auto-detect the sets matching the chosen mesh)", null));
        foreach (ExportWorkflow.AnimSetEntry set in _animSets) _animSetCombo.Items.Add(new AnimSetChoice(set.ToString()!, set));
        _animSetCombo.SelectedIndex = 0;
        RefreshAnimNamesList();

        _outputEditedByUser = false;
        _exportButton.Enabled = false;

        Log("");
        Log($"Opened {Path.GetFileName(path)} — {_meshes.Count} mesh(es), {_animSets.Count} AnimSet(s).");
        if (_meshes.Count == 0) Log("No readable SkeletalMesh objects in this package.");
        if (_animSets.Count == 0)
        {
            Log(
                "This package holds no AnimSet objects. This game usually keeps a character's animations " +
                "in a separate package from its mesh — open that one instead if this mesh has no animations here.");
        }
    }

    private void OnMeshSelected(object? sender, EventArgs e)
    {
        if (_meshList.SelectedItem is not ExportWorkflow.MeshEntry mesh || _packagePath is null)
        {
            _exportButton.Enabled = false;
            return;
        }

        _exportButton.Enabled = true;

        if (!_outputEditedByUser)
        {
            _suppressOutputTracking = true;
            _outputBox.Text = ExportWorkflow.DefaultOutputDirectory(_packagePath, mesh.Name);
            _suppressOutputTracking = false;
        }
    }

    private void OnAnimSetSelected(object? sender, EventArgs e) => RefreshAnimNamesList();

    /// <summary>
    /// Fills the reference list with every animation name in the currently
    /// picked set (or every name across every set, for "(all)") — purely to
    /// help pick a filter term, so this never touches the actual track data,
    /// just the cheap sequence-name lookup.
    /// </summary>
    private void RefreshAnimNamesList()
    {
        _animNamesList.Items.Clear();
        if (_package is null) return;

        IReadOnlyList<string> names = _animSetCombo.SelectedItem is AnimSetChoice { Entry: { } chosen }
            ? ExportWorkflow.ListSequenceNames(_package, chosen.Info)
            : ExportWorkflow.ListAllSequenceNames(_package, _animSets.Select(a => a.Info));

        foreach (string name in names) _animNamesList.Items.Add(name);
    }

    private void OnBrowseOutputClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose an output folder" };
        if (!string.IsNullOrWhiteSpace(_outputBox.Text) && Directory.Exists(_outputBox.Text))
            dialog.SelectedPath = _outputBox.Text;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _outputEditedByUser = true;
        _outputBox.Text = dialog.SelectedPath;
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        if (_package is null || _meshList.SelectedItem is not ExportWorkflow.MeshEntry meshEntry) return;

        string outputDirectory = _outputBox.Text.Trim();
        if (outputDirectory.Length == 0)
        {
            MessageBox.Show(this, "Choose an output folder first.", "No output folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        List<AnimObjectReader.AnimSetInfo> animSetsToExport = _animSetCombo.SelectedItem is AnimSetChoice { Entry: { } chosen }
            ? [chosen.Info]
            : _animSets.Select(a => a.Info).ToList();

        string? filter = string.IsNullOrWhiteSpace(_filterBox.Text) ? null : _filterBox.Text.Trim();

        Package package = _package;
        SkeletalMesh mesh = meshEntry.Mesh;

        _exportButton.Enabled = false;
        _openButton.Enabled = false;
        Log("");
        Log($"--- Exporting '{mesh.Name}' to {outputDirectory} ---");

        ExportWorkflow.ExportSummary summary;
        try
        {
            summary = await Task.Run(() => ExportWorkflow.Export(
                package, mesh, animSetsToExport, outputDirectory, filter,
                log: Log,
                logError: s => Log("ERROR: " + s)));
        }
        catch (Exception ex)
        {
            Log($"Export failed: {ex.Message}");
            _exportButton.Enabled = true;
            _openButton.Enabled = true;
            return;
        }

        _exportButton.Enabled = true;
        _openButton.Enabled = true;

        Log("");
        Log($"{summary.Exported} animation(s) exported to {outputDirectory}");
        if (summary.SkippedNoOverlap > 0)
            Log($"({summary.SkippedNoOverlap} AnimSet(s) shared no bone name with '{mesh.Name}' and were skipped.)");
        if (summary.SkippedNoTracks > 0)
            Log($"({summary.SkippedNoTracks} sequence(s) decoded with no usable bone tracks — an undecoded compression scheme.)");
        if (summary.SkippedExportFailed > 0)
            Log($"({summary.SkippedExportFailed} sequence(s) failed to export — see above.)");
    }

    /// <summary>Appends one line to the log, safe to call from any thread.</summary>
    private void Log(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Log(line)));
            return;
        }

        _log.AppendText(line + Environment.NewLine);
    }
}
