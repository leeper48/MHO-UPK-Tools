using System.Runtime.CompilerServices;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The shortcuts' windows (Kurt, 2026-10-25): Settings → Keyboard Shortcuts (every shortcut and 3D mouse binding, changeable,
/// conflicts in red), the capture window that takes a new one (and says what it clashes with before it's kept), and a
/// right-click on any button: Assign Shortcut / Clear Shortcut. A button with a command of its own (Paint, Weights, Apply
/// Changes …) changes that command; any other button gets a shortcut of its own ("button:&lt;window&gt;/&lt;name&gt;"), which
/// presses it while it's visible and enabled in the active window (not while typing).
/// </summary>
static class ShortcutsUi
{
    /// <summary>The main window's settings (the one copy that's saved: MainForm's).</summary>
    public static Settings? AppSettings;

    static readonly ConditionalWeakTable<Control, string> attached = new();

    /// <summary>The button runs <paramref name="id"/>: its right-click changes that command's shortcut.</summary>
    public static T Attach<T>(T c, string id) where T : Control { attached.AddOrUpdate(c, id); Ui.Retip(c); return c; }

    /// <summary>The command a control runs (Attach), or null.</summary>
    public static string? AttachedTo(Control c) => attached.TryGetValue(c, out var id) ? id : null;

    /// <summary>A button's own shortcut id: its window and its name (an icon button's accessible name, else its text).</summary>
    public static string ButtonId(Button b)
    {
        string form = b.FindForm()?.GetType().Name ?? "";
        string name = (b.AccessibleName is { Length: > 0 } an ? an : b.Text).Replace("▾", "").Replace("&", "").Trim();
        return $"button:{form}/{name}";
    }

    static string ButtonName(Button b) => (b.AccessibleName is { Length: > 0 } an ? an : b.Text).Replace("▾", "").Replace("&", "").Trim();

    static bool installed;
    public static void Install() { if (installed) return; installed = true; Application.AddMessageFilter(new Filter()); }

    static void Store(string id, string? binding)
    {
        var s = AppSettings ?? Settings.Load();
        s.Shortcuts ??= new(StringComparer.OrdinalIgnoreCase);
        var cmd = Shortcuts.Find(id);
        if (binding == null || cmd != null && Shortcuts.Normalize(binding) == Shortcuts.Normalize(cmd.Default)) s.Shortcuts.Remove(id);
        else s.Shortcuts[id] = binding;
        if (s.Shortcuts.Count == 0) s.Shortcuts = null;
        s.Save();
        Shortcuts.Load(s.Shortcuts);
        Ui.RefreshShortcutTips();
    }

    sealed class Filter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x205)   // WM_RBUTTONUP over a button: its shortcut menu
            {
                if (Control.FromHandle(m.HWnd) is Button b && b.ContextMenuStrip == null && b.FindForm() is { } owner)
                {
                    ShowMenu(b, owner);
                    return true;
                }
                return false;
            }
            if (m.Msg is 0x100 or 0x104)   // WM_KEYDOWN, WM_SYSKEYDOWN: a button's own shortcut presses it
            {
                var keyData = (Keys)(int)m.WParam | Control.ModifierKeys;
                string text = Shortcuts.KeyText(keyData);
                if (text.Length == 0 || Shortcuts.ButtonBindings().Count == 0) return false;
                var form = Form.ActiveForm;
                if (form == null || Typing(form)) return false;
                foreach (var (id, binding) in Shortcuts.ButtonBindings())
                {
                    if (!binding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Shortcuts.Normalize).Contains(text, StringComparer.OrdinalIgnoreCase)) continue;
                    if (FindButton(form, id) is { } b) { b.PerformClick(); return true; }
                }
            }
            return false;
        }

        static bool Typing(Form f)
        {
            Control? c = f.ActiveControl;
            while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
            return c is TextBoxBase || c is DataGridView { IsCurrentCellInEditMode: true };
        }

        static Button? FindButton(Control root, string id)
        {
            foreach (Control c in root.Controls)
            {
                if (c is Button b && b.Visible && b.Enabled && ButtonId(b).Equals(id, StringComparison.OrdinalIgnoreCase)) return b;
                if (c.Visible && c.HasChildren && FindButton(c, id) is { } found) return found;
            }
            return null;
        }
    }

    static void ShowMenu(Button b, Form owner)
    {
        string id = attached.TryGetValue(b, out var cid) ? cid : ButtonId(b);
        var cmd = Shortcuts.Find(id) ?? Shortcuts.ButtonCommand(id, ButtonName(b));
        string now = Shortcuts.Text(id);
        var menu = new ContextMenuStrip();
        menu.Items.Add(now.Length > 0 ? $"Change Shortcut ({now})" : "Assign Shortcut", null, (_, _) =>
        {
            if (Capture(owner, cmd) is string nb) { Store(id, nb); Ui.Tip(b, null); }
        });
        if (now.Length > 0 && Shortcuts.Find(id) is { } known && Shortcuts.Normalize(now) != Shortcuts.Normalize(known.Default))
            menu.Items.Add($"Back to the Default ({known.Default})", null, (_, _) => Store(id, null));
        else if (now.Length > 0 && Shortcuts.Find(id) == null)
            menu.Items.Add("Clear Shortcut", null, (_, _) => Store(id, null));
        menu.Items.Add("Keyboard Shortcuts", null, (_, _) => { using var f = new ShortcutsForm(); f.ShowDialog(owner); });
        Ui.ShowAt(menu, Cursor.Position);
    }

    /// <summary>The capture window: the new binding for <paramref name="cmd"/> (null: cancelled).</summary>
    public static string? Capture(IWin32Window owner, Shortcuts.Command cmd, Func<string, string>? others = null)
    {
        using var f = new CaptureForm(cmd, others ?? Shortcuts.Text);
        return f.ShowDialog(owner) == DialogResult.OK ? f.Result : null;
    }

    /// <summary>Asks for one binding: keys pressed, or (for mouse commands) a click / drag start / wheel turn in the box, or
    /// (for held modifiers) the modifier keys; what it would clash with is shown at once.</summary>
    sealed class CaptureForm : Form
    {
        readonly Shortcuts.Command cmd;
        readonly Func<string, string> others;
        readonly Label shown = new() { AutoSize = true, Font = Ui.Bold(13f), Margin = new Padding(0, 6, 0, 6), Text = "(Press a Shortcut)" };
        readonly Label clash = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 6), ForeColor = Ui.Warn };
        readonly Button ok;
        public string? Result { get; private set; }

        public CaptureForm(Shortcuts.Command cmd, Func<string, string> others)
        {
            this.cmd = cmd; this.others = others;
            Text = "Shortcut: " + cmd.Name;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Ui.DarkFrame(this);
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            Font = Ui.Regular(9.5f); Padding = new Padding(14); KeyPreview = true;
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true };
            t.Controls.Add(new Label { Text = cmd.Name, AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) });
            string how = cmd.Kind switch
            {
                Shortcuts.Kind.Key => "Press the keys for it (with Ctrl, Alt or Shift if you like).",
                Shortcuts.Kind.Held => "Hold the modifier key(s) for it: Ctrl, Alt, Shift or two of them.",
                Shortcuts.Kind.Wheel => "Turn the mouse wheel in the box below, holding the modifier keys you want with it.",
                Shortcuts.Kind.MouseClick => "Click in the box below with the button and modifier keys you want (double-click for a double-click).",
                _ => "Press the mouse button in the box below, holding the modifier keys you want: dragging with it does this.",
            };
            t.Controls.Add(new Label { Text = how + $"{Environment.NewLine}Now: {(Shortcuts.Text(cmd.Id) is { Length: > 0 } n ? n : "(none)")}" + (Shortcuts.Find(cmd.Id) is { Default.Length: > 0 } d ? $" · Default: {d.Default}" : ""), AutoSize = true, MaximumSize = new Size(460, 0), Tag = "subtle" });
            if (cmd.Kind is not (Shortcuts.Kind.Key or Shortcuts.Kind.Held))
            {
                var box = new Panel { Height = 70, Dock = DockStyle.Top, BackColor = Ui.Card, Margin = new Padding(0, 8, 0, 4), Cursor = Cursors.Hand };
                box.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, "Click, drag or scroll here", Font, box.ClientRectangle, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                box.MouseDown += (_, e) =>
                {
                    string btn = e.Button switch { MouseButtons.Left => "Left", MouseButtons.Right => "Right", MouseButtons.Middle => "Middle", MouseButtons.XButton1 => "Back", MouseButtons.XButton2 => "Forward", _ => "" };
                    if (btn.Length == 0) return;
                    string mods = Mods(ModifierKeys);
                    if (cmd.Kind == Shortcuts.Kind.MouseDrag) Set(mods + btn + " Drag");
                    else if (e.Clicks >= 2) Set(mods + "Double Click");
                    else Set(mods + (btn == "Left" ? "Click" : btn + " Click"));
                };
                box.MouseWheel += (_, _) => { if (cmd.Kind == Shortcuts.Kind.Wheel) Set(Mods(ModifierKeys) + "Wheel"); };
                box.MouseEnter += (_, _) => box.Focus();
                t.Controls.Add(box);
            }
            t.Controls.Add(shown);
            t.Controls.Add(clash);
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 0, 0) };
            ok = Ui.AccentButton("Assign", () => { Result = Shown(); DialogResult = DialogResult.OK; }, tip: "Keep this shortcut.");
            ok.Enabled = false;
            var cancel = Ui.FlatButton("Cancel", () => DialogResult = DialogResult.Cancel, tip: "Leave the shortcut as it was (the keys you press are taken as the new shortcut, so Esc can be one: use this button).");
            buttons.Controls.AddRange([cancel, ok]);
            if (Shortcuts.Find(cmd.Id) is { } known && known.Default.Length > 0)
                buttons.Controls.Add(Ui.FlatButton("Default", () => Set(known.Default), tip: "Put the default back: " + known.Default));
            t.Controls.Add(buttons);
            Controls.Add(t);
            KeyDown += (_, e) =>
            {
                e.Handled = e.SuppressKeyPress = true;
                if (cmd.Kind == Shortcuts.Kind.Key && Shortcuts.KeyText(e.KeyData) is { Length: > 0 } k) Set(k);
                else if (cmd.Kind == Shortcuts.Kind.Held && Mods(e.Modifiers).TrimEnd('+') is { Length: > 0 } h) Set(h);
            };
            Theme.Apply(this, Palette.Dark); Modern.Modernize(this); Ui.Restyle(this);
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; MinimumSize = new Size(420, 0);
        }

        static string Mods(Keys k) => ((k & Keys.Control) != 0 ? "Ctrl+" : "") + ((k & Keys.Alt) != 0 ? "Alt+" : "") + ((k & Keys.Shift) != 0 ? "Shift+" : "");

        string Shown() => shown.Text;

        void Set(string binding)
        {
            shown.Text = Shortcuts.Normalize(binding);
            ok.Enabled = true;
            var c = Shortcuts.Conflicts(cmd, id => id == cmd.Id ? shown.Text : others(id));
            clash.Text = c.Count == 0 ? "" : "Also used by: " + string.Join("; ", c.Select(x => $"{x.Other.Name} ({Shortcuts.AreaName(x.Other.Area)})")) + Environment.NewLine + "Assign keeps both: change one of them, or the first found wins.";
            clash.ForeColor = Ui.Warn;
            shown.ForeColor = c.Count == 0 ? Ui.Text : Ui.Warn;
        }
    }
}

/// <summary>Settings → Keyboard Shortcuts: every command, its area and shortcut, conflicts in red; double-click (or Change)
/// to take a new one; Default puts one back, Reset All every one.</summary>
sealed class ShortcutsForm : Form
{
    readonly DataGridView grid;
    readonly Label heading = new() { Text = "Keyboard Shortcuts", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) };
    readonly Label summary = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 8) };
    List<Shortcuts.Command> rows = [];

    public ShortcutsForm()
    {
        Text = "Keyboard Shortcuts";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f); Padding = new Padding(14);
        float s = Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(heading, 0, 0);
        t.Controls.Add(summary, 0, 1);
        grid = Ui.Grid(s, false, ("Where", 130), ("Action", 0), ("Shortcut", 200), ("Conflict", 230));
        Ui.StyleGrid(grid);
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Change(e.RowIndex); };
        t.Controls.Add(grid, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Change", () => { if (grid.CurrentCell != null) Change(grid.CurrentCell.RowIndex); }, tip: "Take a new shortcut for the selected row (or double-click the row)."),
            Ui.FlatButton("Default", () => { if (grid.CurrentCell != null) Reset(grid.CurrentCell.RowIndex); }, tip: "The selected row back to its default (a button's own shortcut: removed)."),
            Ui.FlatButton("Reset All", ResetAll, tip: "Every shortcut back to its default; buttons' own shortcuts removed."),
            Ui.AccentButton("Close", Close, tip: "Close this window (Esc). Changes are kept as you make them."),
        ]);
        t.Controls.Add(buttons, 0, 3);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this); Ui.Restyle(this);
        Ui.FitToScreen(this, 900, 640);
        Fill();
    }

    void Fill()
    {
        int keep = grid.CurrentCell?.RowIndex ?? 0;
        rows = [.. Shortcuts.Everything()];
        grid.Rows.Clear();
        int clashes = 0;
        foreach (var c in rows)
        {
            var conf = Shortcuts.Conflicts(c, Shortcuts.Text);
            int i = grid.Rows.Add(Shortcuts.AreaName(c.Area), c.Name, Shortcuts.Text(c.Id) is { Length: > 0 } b ? b : "(none)",
                conf.Count == 0 ? "" : string.Join("; ", conf.Select(x => $"{x.Binding}: {x.Other.Name} ({Shortcuts.AreaName(x.Other.Area)})")));
            grid.Rows[i].Cells[1].ToolTipText = c.Description;
            bool changed = Shortcuts.Find(c.Id) is { } k && Shortcuts.Normalize(Shortcuts.Text(c.Id)) != Shortcuts.Normalize(k.Default);
            if (changed) grid.Rows[i].Cells[2].Style.ForeColor = Ui.Accent;
            if (conf.Count > 0) { grid.Rows[i].Cells[3].Style.ForeColor = Ui.Warn; clashes++; }
        }
        summary.Text = $"{rows.Count} shortcuts · {(clashes == 0 ? "No Conflicts" : $"{clashes} with a conflict (red)")} · changed ones in blue. Double-click a row to change it; right-click any button in the app to give it a shortcut.";
        if (grid.Rows.Count > 0) grid.CurrentCell = grid.Rows[Math.Clamp(keep, 0, grid.Rows.Count - 1)].Cells[1];
    }

    void Change(int row)
    {
        if (row < 0 || row >= rows.Count) return;
        if (ShortcutsUi.Capture(this, rows[row]) is string b) { Store(rows[row].Id, b); Fill(); }
    }

    void Reset(int row)
    {
        if (row < 0 || row >= rows.Count) return;
        Store(rows[row].Id, null); Fill();
    }

    void ResetAll()
    {
        if (Dialog.Show(this, "Put every shortcut back to its default, and remove the shortcuts given to buttons?", "Reset All Shortcuts", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var s = ShortcutsUi.AppSettings ?? Settings.Load();
        s.Shortcuts = null; s.Save(); Shortcuts.Load(null); Ui.RefreshShortcutTips();
        Fill();
    }

    static void Store(string id, string? binding)
    {
        var s = ShortcutsUi.AppSettings ?? Settings.Load();
        s.Shortcuts ??= new(StringComparer.OrdinalIgnoreCase);
        var cmd = Shortcuts.Find(id);
        if (binding == null || cmd != null && Shortcuts.Normalize(binding) == Shortcuts.Normalize(cmd.Default)) s.Shortcuts.Remove(id);
        else s.Shortcuts[id] = binding;
        if (s.Shortcuts.Count == 0) s.Shortcuts = null;
        s.Save();
        Shortcuts.Load(s.Shortcuts);
        Ui.RefreshShortcutTips();
    }
}
