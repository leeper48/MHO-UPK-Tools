using System.Diagnostics;
using MhoExtendedModManager.FreeCam;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The FreeCam Options tab: options on top of Marvel Heroes FreeCam (M.H.F.C.) by Michael (MichaelMerc on Nexus). Shows
/// whether Freecam is installed (with a link to its Nexus page and the credit), the keys / gamepad buttons, zoom, camera and
/// Behind-mode settings, and Apply / Remove (FreeCam.FreeCamBuddy does the work; nothing here writes files).
/// </summary>
sealed class FreeCamView : UserControl
{
    readonly Func<string?> gameRoot;
    readonly Func<IReadOnlyList<(string Name, string Copy)>> modsNamingIt;
    readonly FreeCamSettings cfg = FreeCamSettings.Load();
    static readonly FreeCamSettings Defaults = new();
    readonly Label statusDot = new() { AutoSize = true, Font = Ui.Bold(12f), Text = "●" };
    readonly Label statusText = new() { AutoSize = true, Font = Ui.Bold(10.5f) };
    readonly Label statusDetail = new() { AutoSize = true, Tag = "subtle" };
    readonly Label zoomInfo = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 0), MaximumSize = new Size(560, 0) };
    readonly Label footer = new() { AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle" };
    readonly Button applyButton, removeButton;
    readonly List<Action> refreshers = [];
    FreeCamBuddy.Status? status;
    bool busy;

    public FreeCamView(Func<string?> gameRoot, Func<IReadOnlyList<(string Name, string Copy)>> modsNamingIt)
    {
        this.gameRoot = gameRoot;
        this.modsNamingIt = modsNamingIt;
        Dock = DockStyle.Fill;
        Font = Ui.Regular(9.5f);
        float s = Ui.Dpi(DeviceDpi);
        int maxText = (int)(1100 * s);

        // ---- header: title, credit, Freecam status
        var title = new Label { Text = "FreeCam Options", AutoSize = true, Font = Ui.Bold(15f), Margin = new Padding(0, 0, 0, 2) };
        var credit = new Label
        {
            AutoSize = true, Tag = "subtle", MaximumSize = new Size(maxText, 0), Margin = new Padding(0, 0, 0, 6),
            Text = "Options for Marvel Heroes FreeCam (M.H.F.C.) by Michael (MichaelMerc on Nexus). The orbit camera itself, RB height, " +
                   "the mouse camera and the visible walls are his work: FreeCam Options only adds camera modes, invert Y, more zoom and " +
                   "limits on top of it, and needs his mod installed (its Full option).",
        };
        var nexus = Ui.FlatButton("Freecam on Nexus", () => Process.Start(new ProcessStartInfo(FreeCamBuddy.NexusUrl) { UseShellExecute = true }),
            "Open Marvel Heroes FreeCam (M.H.F.C.) by MichaelMerc on Nexus Mods in your browser, to download or update it.");
        var recheck = Ui.FlatButton("Check Again", RefreshStatus, "Look at the game's MarvelGame.upk and Freecam's loader again (after installing or updating Freecam).");
        var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        statusRow.Controls.AddRange([statusDot, statusText, nexus, recheck]);
        statusDot.Margin = new Padding(0, 4, 2, 0); statusText.Margin = new Padding(0, 7, 12, 0);
        statusDetail.MaximumSize = new Size(maxText, 0);
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 10, 12, 6) };
        head.Controls.AddRange([title, credit, statusRow, statusDetail]);

        // ---- cards (Kurt, 2026-11-09: use the width; explanations behind the ⓘ on each card): Controls (two columns wide),
        // Options and View Distance across the top, then Zoom / Camera / Locked Behind / Default MHO, then Snap Behind / Selfie
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4, AutoScroll = true, Padding = new Padding(8, 0, 8, 0) };
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // the space left over (the cards keep their own height)
        // the top row: Controls as wide as its table needs, Options and View Distance sharing the rest
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        var controlsCol = new ColumnStyle(SizeType.Absolute, 900 * s);
        top.ColumnStyles.Add(controlsCol); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var controlsTable = ControlsBody(s);
        var controlsCard = Card("CONTROLS", controlsTable, ControlsInfo);
        // its column is the table's own width (a docked card in an auto-sized column reports none)
        top.Layout += (_, _) =>
        {
            int w = controlsTable.GetPreferredSize(Size.Empty).Width + controlsCard.Padding.Horizontal + controlsCard.Margin.Horizontal + 4;
            if (Math.Abs(controlsCol.Width - w) > 1) controlsCol.Width = w;
        };
        controlsCard.Dock = DockStyle.Fill;
        top.Controls.Add(controlsCard, 0, 0);
        top.Controls.Add(Card("OPTIONS", OptionsBody(s), OptionsInfo), 1, 0);
        grid.Controls.Add(top, 0, 0);
        grid.SetColumnSpan(top, 4);
        grid.Controls.Add(Card("ZOOM", ZoomBody(s), "The mouse wheel carries on past the game's closest and farthest zoom steps, by the extra steps allowed here. Scrolling back takes the extra steps off first."), 0, 1);
        grid.Controls.Add(Card("CAMERA", Stack(s,
            Slider(s, "Tilt Down Limit", 10, 89, 1, () => cfg.TiltDown, v => cfg.TiltDown = v, v => $"{v:0}°", 82, "How far the camera can look down from above (Freecam has no limit; past 90° it flips over)."),
            Slider(s, "Tilt Up Limit", 0, 60, 1, () => cfg.TiltUp, v => cfg.TiltUp = v, v => $"{v:0}°", 22, "How far the camera can look up from below the hero."),
            Slider(s, "Orbit Speed", 25, 300, 5, () => cfg.OrbitSpeed, v => cfg.OrbitSpeed = v, v => $"{v:0} %", 100, "How fast the right stick / mouse camera turns the view (100 % = Freecam's speed).")),
            "Limits and speed for Free Orbit and Locked Behind: how far the camera may tilt down and up, and how fast the right stick or mouse camera turns it."), 1, 1);
        grid.Controls.Add(Card("LOCKED BEHIND MODE", Stack(s, [
            Slider(s, "Shoulder Offset", -150, 150, 5, () => cfg.ShoulderOffset, v => cfg.ShoulderOffset = v, v => v == 0 ? "Centered" : v > 0 ? $"{v:0} Right" : $"{-v:0} Left", 0, "Moves the view sideways so the camera looks over the hero's shoulder (right is positive)."),
            Slider(s, "Follow Speed", 5, 200, 5, () => cfg.FollowSpeed, v => cfg.FollowSpeed = v, v => $"{v / 10:0.0}", 50, "How quickly the camera swings behind the hero when they turn."),
            Slider(s, "Start Tilt", -20, 60, 1, () => cfg.BehindTilt, v => cfg.BehindTilt = v, v => $"{v:0}°", 10, "How far the camera looks down when Locked Behind is switched on (the stick still tilts it unless Lock Tilt is ticked)."),
            Check("Lock Tilt", "Ticked: Locked Behind stays at its Start Tilt; the stick and mouse camera don't tilt it. Unticked: they tilt it as in Free Orbit.", () => cfg.BehindLockTilt, v => cfg.BehindLockTilt = v),
            .. ZoomRows(s, "Locked Behind", () => cfg.BehindKeepZoom, v => cfg.BehindKeepZoom = v, () => cfg.BehindZoomX, v => cfg.BehindZoomX = v, Defaults.BehindZoomX,
                "The zoom Locked Behind starts at, like a camera zoom: right = closer. 1 × is the game's closest view, 2 × twice as close, 0.5 × twice as far.  (Double-click: back to the default.)")]),
            "Locked Behind follows the way the hero faces. Movement stays camera-relative (as in Freecam), so strafing turns the hero and the camera together. Lock Tilt holds the camera at the Start Tilt. Keep Current In-Game Zoom leaves the zoom when you switch to it; unticked, it starts at the zoom shown (1 × is the game's closest view)."), 2, 1);
        grid.Controls.Add(Card("SNAP BEHIND", ShortcutBody(s, "Snap Behind", () => cfg.SnapTilt, v => cfg.SnapTilt = v, () => cfg.SnapHeight, v => cfg.SnapHeight = v,
            () => cfg.SnapKeepZoom, v => cfg.SnapKeepZoom = v, () => cfg.SnapZoomX, v => cfg.SnapZoomX = v, Defaults.SnapTilt, Defaults.SnapHeight, Defaults.SnapZoomX),
            "Snap Behind / Free Orbit swings the camera behind your hero once, at this tilt and height, and switches to Free Orbit (free to turn again), from any mode. Keep Current In-Game Zoom leaves the zoom; unticked, it sets the zoom shown (1 × is the game's closest view)."), 0, 2);
        grid.Controls.Add(Card("SELFIE", ShortcutBody(s, "Selfie", () => cfg.SelfieTilt, v => cfg.SelfieTilt = v, () => cfg.SelfieHeight, v => cfg.SelfieHeight = v,
            () => cfg.SelfieKeepZoom, v => cfg.SelfieKeepZoom = v, () => cfg.SelfieZoomX, v => cfg.SelfieZoomX = v, Defaults.SelfieTilt, Defaults.SelfieHeight, Defaults.SelfieZoomX),
            "Selfie puts the camera in front of your hero, facing them, at this tilt and height, and switches to Free Orbit. Keep Current In-Game Zoom leaves the zoom; unticked, it sets the zoom shown."), 1, 2);
        grid.Controls.Add(Card("DEFAULT MHO MODE", DefaultBody(s),
            "The game's own camera direction. Game's Tilt, a Height of Normal and Keep Current In-Game Zoom (the defaults) make it the base game's camera. Untick Game's Tilt to set how far it looks down, move Height to raise or lower it (RB + stick doesn't in this mode), Keep Current In-Game Zoom leaves the zoom when you switch to it; untick it to start at the zoom shown (as on Snap Behind: 1 × is the game's closest view). The game's own zoom step nearest that distance is used, so its angle and height apply."), 3, 1);

        // ---- view distance (Kurt, 2026-11-08): the server's own chat command, the only client-side way to see farther
        const string aoiCommand = "!aoi volume 5000";
        // (no Copy button: the game's chat box doesn't take pasted text, Kurt 2026-11-08)
        var aoiRow = new Label { Text = aoiCommand, AutoSize = true, Font = Ui.Bold(13f), Margin = new Padding(0, 4, 0, 0) };
        top.Controls.Add(Card("VIEW DISTANCE", Stack(s, Note(s, "To see farther, type this in the game's chat:"), aoiRow),
            "Buildings, enemies and props farther away pop in because the server only sends the part of the zone around you: its default is 3200. " +
            "This command has it send more (up to 5000, about 2.4× the area), and the server keeps it for your account. Type it: the chat box doesn't take pasted text. " +
            "\"!aoi volume\" shows yours, \"!aoi volume reset\" puts it back. It's the server's own command, so a server that turned it off won't take it."));

        // ---- bottom bar
        applyButton = Ui.AccentButton("Apply to Game", Apply, "Build MarvelGame.upk from your Freecam with these settings (and MarvelHUD_SF.upk for hidden labels), check them and write them into the game (undo history kept), and set the keys in the game's input settings. The game must be closed.");
        removeButton = Ui.FlatButton("Remove", Remove, "Put Freecam's own MarvelGame.upk back (exactly as its installer left it), show the game's overhead labels again and take FreeCam Options's keys out of the game's input settings.");
        var defaults = Ui.FlatButton("Reset to Defaults", ResetDefaults, "Every setting on this tab back to FreeCam Options's defaults (nothing is written until Apply to Game).");
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 6, 10, 6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        left.Controls.AddRange([removeButton, defaults]);
        bottom.Controls.Add(left, 0, 0); bottom.Controls.Add(footer, 1, 0); bottom.Controls.Add(applyButton, 2, 0);
        applyButton.Anchor = AnchorStyles.Right;
        Bars = [bottom];

        Controls.Add(grid); Controls.Add(bottom); Controls.Add(head);
        UpdateZoomInfo();
        RefreshStatus();
    }

    public Control[] Bars { get; }

    // ------------------------------------------------------------------ controls card

    /// <summary>One action's settings, for the Controls rows.</summary>
    sealed record ActionRow(string Name, string What, Func<string> GetKey, Action<string> SetKey, Func<string> GetPad, Action<string> SetPad, Func<string> GetPress, Action<string> SetPress);

    Control ControlsBody(float s)
    {
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, RowCount = 11, Dock = DockStyle.Top };
        for (int i = 0; i < 3; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.Controls.Add(Head("Action"), 0, 0); t.Controls.Add(Head("Keyboard"), 1, 0); t.Controls.Add(Head("Gamepad"), 2, 0);
        ActionRow[] rows =
        [
            new("Cycle Camera Mode", "Cycles Free Orbit → Locked Behind → Default MHO (the game's own camera direction).",
                () => cfg.ModeKey, v => cfg.ModeKey = v, () => cfg.ModePad, v => cfg.ModePad = v, () => cfg.ModePress, v => cfg.ModePress = v),
            new("Snap Behind / Free Orbit", "Puts the camera behind your hero at once and switches to Free Orbit (free to turn again), from any mode.",
                () => cfg.SnapKey, v => cfg.SnapKey = v, () => cfg.SnapPad, v => cfg.SnapPad = v, () => cfg.SnapPress, v => cfg.SnapPress = v),
            new("Selfie", "Puts the camera in front of your hero, facing them, and switches to Free Orbit (free to turn again).",
                () => cfg.SelfieKey, v => cfg.SelfieKey = v, () => cfg.SelfiePad, v => cfg.SelfiePad = v, () => cfg.SelfiePress, v => cfg.SelfiePress = v),
            new("Locked Behind", "Switches straight to Locked Behind: the camera follows the way your hero faces, starting at Locked Behind's Start Tilt.",
                () => cfg.BehindKey, v => cfg.BehindKey = v, () => cfg.BehindPad, v => cfg.BehindPad = v, () => cfg.BehindPress, v => cfg.BehindPress = v),
            new("Default MHO", "Switches straight to Default MHO: the game's own camera direction, at the normal height and zoom.",
                () => cfg.IsoKey, v => cfg.IsoKey = v, () => cfg.IsoPad, v => cfg.IsoPad = v, () => cfg.IsoPress, v => cfg.IsoPress = v),
        ];
        for (int i = 0; i < rows.Length; i++) BindRow(t, 1 + i * 2, s, rows[i], rows);
        return t;
    }

    const string ControlsInfo =
        "A key and a gamepad button for each action. Gamepad presses: short = a quick press, long = held for the Long Press Time (it acts while you hold), " +
        "double = two presses close together, short then long = a tap, then press again and hold. None = the gamepad doesn't do that action.\n\n" +
        "Actions can share a button (and RT setting) with different presses, e.g. a short press to snap, a double press for a selfie and a long press to change mode; " +
        "up to three different buttons can be used. A short press waits a moment for a second one only when another action on that button uses a double press.\n\n" +
        "Freecam's own controls stay as they are: the right stick or mouse camera orbits, RB + stick (or Page Up / Down) raises and lowers the camera. " +
        "A key or button chosen here overrides what it did before; Remove gives it back.";

    const string OptionsInfo =
        "Invert Y flips up / down for the right stick and the mouse camera, every session.\n\n" +
        "Remember Camera on Launch: the game starts, and changes zones, with the camera mode, zoom and angle you last had (the game's script saves them in its settings file when they change and the camera is at rest).\n\n" +
        "Permanent Wall Fade (Disables F11 Toggle): Freecam 1.2 keeps interior walls visible; ticked, they fade as in the base game, and F11 no longer switches them.\n\n" +
        "Overhead Labels: only the text goes, health bars and markers stay. Written into the game's MarvelHUD_SF.upk on Apply to Game; untick and Apply, or Remove, to put the game's labels back.";

    /// <summary>Invert Y, Remember Camera on Launch, the long-press time and the overhead labels.</summary>
    Control OptionsBody(float s)
    {
        var invert = new CheckBox { Text = "Invert Y (Every Session)", AutoSize = true, Checked = cfg.InvertByDefault, Margin = new Padding(0, 4, 0, 0) };
        Ui.Tip(invert, "Up / down flipped for the right stick and the mouse camera, every session, until you untick it and Apply to Game again.");
        invert.CheckedChanged += (_, _) => { cfg.InvertByDefault = invert.Checked; Changed(); };
        refreshers.Add(() => invert.Checked = cfg.InvertByDefault);
        var remember = new CheckBox { Text = "Remember Camera on Launch", AutoSize = true, Checked = cfg.RememberCamera, Margin = new Padding(0, 6, 0, 0) };
        Ui.Tip(remember, "The game starts (and changes zones) with the camera mode, zoom and angle you last had.");
        remember.CheckedChanged += (_, _) => { cfg.RememberCamera = remember.Checked; Changed(); };
        refreshers.Add(() => remember.Checked = cfg.RememberCamera);
        var walls = Check("Permanent Wall Fade\n(Disables F11 Toggle)", "Ticked: interior walls fade when the camera comes close (the game's own way), and F11 no longer switches them. Unticked: Freecam 1.2's default, always visible.", () => cfg.WallsFade, v => cfg.WallsFade = v);
        // two lines: on one, the Options column cut it off (manual screenshot, 2026-11-10); a fixed size, as AutoSize draws one line
        walls.AutoSize = false;
        walls.Size = new Size((int)(240 * s), (int)(44 * s));
        var longPress = Slider(s, "Long Press Time", 200, 3000, 50, () => cfg.LongPressMs, v => cfg.LongPressMs = v, v => $"{v:0} ms", Defaults.LongPressMs,
            "How long a gamepad button is held for a long press (and for the hold of Short Then Long).");
        longPress.Margin = new Padding(0, 8, 0, 2);
        var labelsHead = new Label { Text = "OVERHEAD LABELS", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, 12, 0, 2) };
        return Stack(s, invert, remember, walls, longPress, labelsHead,
            Check("Hide Names", "No names over characters (enemies, vendors, allies; other players' and your own too, as they use the same label).", () => cfg.HideNames, v => cfg.HideNames = v),
            Check("Hide Titles", "No title line under the names (\"Vendor\", \"Crafter\" …).", () => cfg.HideTitles, v => cfg.HideTitles = v),
            Check("Hide Guild Names", "No guild name under player names.", () => cfg.HideGuilds, v => cfg.HideGuilds = v),
            Check("Hide Level Numbers", "No level number (and its badge) beside the names of players and other characters that show one.", () => cfg.HideLevels, v => cfg.HideLevels = v));
    }




    static Label Head(string text) => new() { Text = text, AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, 0, 10, 4) };

    static readonly string[] PressTexts = ["None", "Short Press", "Long Press", "Double Press", "Short Then Long"];

    void BindRow(TableLayoutPanel t, int row, float s, ActionRow me, ActionRow[] all)
    {
        var name = Ui.Tip(new Label { Text = me.Name, AutoSize = true, Margin = new Padding(0, 9, 12, 0) }, me.What);

        // keyboard: modifier + key
        var mod = new DropDown { Width = (int)(92 * s), Margin = new Padding(0, 3, 4, 0) };
        mod.Items.AddRange(["None", "Ctrl", "Shift", "Alt"]);
        var key = new DropDown { Width = (int)(110 * s), Margin = new Padding(0, 3, 12, 0) };
        key.Items.AddRange(FreeCamBuddy.Keys.Select(k => k.Text).ToArray());
        // gamepad: button + Hold RT + how it is pressed
        var pad = new DropDown { Width = (int)(150 * s), Margin = new Padding(0, 3, 4, 0) };
        pad.Items.AddRange(FreeCamBuddy.PadButtons.Select(k => k.Text).ToArray());
        var rt = new CheckBox { Text = "RT", AutoSize = true, Margin = new Padding(0, 7, 6, 0) };
        var press = new DropDown { Width = (int)(160 * s), Margin = new Padding(0, 3, 4, 0) };
        press.Items.AddRange(PressTexts);
        Ui.Tip(mod, "A modifier key to hold with it (None = the key alone)."); Ui.Tip(key, $"The key for {me.Name}.");
        Ui.Tip(pad, $"The gamepad button for {me.Name} (None = no gamepad button). Other actions may use the same one with a different press.");
        Ui.Tip(rt, "Hold RT (the gamepad's modifier) with the button, like the game's own RT combinations.");
        Ui.Tip(press, $"How the button is pressed for {me.Name}: short (a tap), long (held for the Long Press Time), double (two taps) or short then long (a tap, then press again and hold). None: the gamepad doesn't do {me.Name}.");
        var keyNote = new Label { AutoSize = true, Tag = "subtle", Font = Ui.Regular(8f), Margin = new Padding(0, 0, 0, 6) };
        var padNote = new Label { AutoSize = true, Tag = "subtle", Font = Ui.Regular(8f), Margin = new Padding(0, 0, 0, 6) };

        bool loading = false;
        void Notes(string? extra = null)
        {
            keyNote.Text = Note(me.GetKey());
            var sharing = all.Where(o => o != me && me.GetPad().Length > 0 && o.GetPad().Equals(me.GetPad(), StringComparison.OrdinalIgnoreCase)).Select(o => o.Name).ToList();
            padNote.Text = extra ?? (sharing.Count > 0 ? $"Shared with {sharing.Count} other action{(sharing.Count > 1 ? "s" : "")}" : Note(me.GetPad()));
            Ui.Tip(padNote, sharing.Count > 0 ? $"This button also does {string.Join(", ", sharing)}: the press tells them apart." : "");
        }
        void Load()
        {
            loading = true;
            var kb = FreeCamBuddy.Bind.Parse(me.GetKey());
            mod.SelectedIndex = kb is not FreeCamBuddy.Bind k ? 0 : k.Ctrl ? 1 : k.Shift ? 2 : k.Alt ? 3 : 0;
            key.SelectedIndex = Math.Max(0, Array.FindIndex(FreeCamBuddy.Keys, x => kb is FreeCamBuddy.Bind k2 && x.Name.Equals(k2.Name, StringComparison.OrdinalIgnoreCase)));
            var pb = FreeCamBuddy.Bind.Parse(me.GetPad());
            pad.SelectedIndex = Math.Max(0, Array.FindIndex(FreeCamBuddy.PadButtons, x => pb is FreeCamBuddy.Bind p && x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)));
            rt.Checked = pb is FreeCamBuddy.Bind p2 && p2.Shift;
            press.SelectedIndex = Math.Max(0, Array.IndexOf(FreeCamSettings.PressTypes, me.GetPress()));
            loading = false;
            Notes();
        }
        void Save()
        {
            if (loading) return;
            string k = FreeCamBuddy.Keys[Math.Max(0, key.SelectedIndex)].Name;
            me.SetKey(k.Length == 0 ? "" : (mod.SelectedIndex switch { 1 => "Ctrl+", 2 => "Shift+", 3 => "Alt+", _ => "" }) + k);
            string p = FreeCamBuddy.PadButtons[Math.Max(0, pad.SelectedIndex)].Name;
            me.SetPad(p.Length == 0 ? "" : (rt.Checked ? "RT+" : "") + p);
            me.SetPress(FreeCamSettings.PressTypes[Math.Max(0, press.SelectedIndex)]);
            // The same button, RT setting and press can't do two actions: the others give it up.
            var clashing = all.Where(o => o != me && me.GetPad().Length > 0 && me.GetPress().Length > 0
                && o.GetPad().Equals(me.GetPad(), StringComparison.OrdinalIgnoreCase) && o.GetPress() == me.GetPress()).ToList();
            foreach (var o in clashing) o.SetPress("");
            Changed();
            foreach (var r in refreshers.ToList()) r();
            if (clashing.Count > 0) Notes($"Same button and press as {string.Join(" and ", clashing.Select(o => o.Name))}: its press was set to None");
        }
        mod.SelectedIndexChanged += (_, _) => Save(); key.SelectedIndexChanged += (_, _) => Save();
        pad.SelectedIndexChanged += (_, _) => Save(); rt.CheckedChanged += (_, _) => Save(); press.SelectedIndexChanged += (_, _) => Save();
        refreshers.Add(Load);

        var kbBox = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty }; kbBox.Controls.AddRange([mod, key]);
        var padBox = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty }; padBox.Controls.AddRange([pad, rt, press]);
        t.Controls.Add(name, 0, row); t.Controls.Add(kbBox, 1, row); t.Controls.Add(padBox, 2, row);
        t.Controls.Add(keyNote, 1, row + 1); t.Controls.Add(padNote, 2, row + 1);
        Load();
    }

    /// <summary>"Now: …" for a key / button: what the game's own settings give it (FreeCam Options's lines left out).</summary>
    static string Note(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return "Not used";
        try { return FreeCamBuddy.CurrentCommand(spec) is string c ? $"Overrides: {Short(c)}" : "Free (unused)"; }
        catch (IOException) { return ""; }
    }

    static string Short(string command) => command.Length > 48 ? command[..46] + "…" : command;

    // ------------------------------------------------------------------ snap behind / selfie cards

    /// <summary>A shortcut's settings: the tilt and camera height it sets, and its zoom (kept, or set to a preset).</summary>
    Control ShortcutBody(float s, string name, Func<int> getTilt, Action<int> setTilt, Func<int> getHeight, Action<int> setHeight,
        Func<bool> getKeep, Action<bool> setKeep, Func<double> getZoom, Action<double> setZoom, int tiltHome, int heightHome, double zoomHome)
    {
        var tilt = Slider(s, "Tilt", -20, 60, 1, getTilt, setTilt, v => $"{v:0}°", tiltHome, $"How far the camera looks down after {name} (negative: from below).");
        var height = Slider(s, "Height", -250, 250, 5, getHeight, setHeight, v => v == 0 ? "Normal" : v > 0 ? $"+{v:0}" : $"{v:0}", heightHome, $"The camera's height after {name}: Freecam's raise / lower (RB + stick), set to this.");
        return Stack(s, [tilt, height, .. ZoomRows(s, name, getKeep, setKeep, getZoom, setZoom, zoomHome,
            "The zoom it sets, like a camera zoom: right = closer. 1 × is the game's closest view, 2 × twice as close, 0.5 × twice as far.  (Double-click: back to the default.)")]);
    }

    /// <summary>Keep Current In-Game Zoom and the Zoom slider it turns off (Snap Behind, Selfie, Locked Behind, Default MHO).</summary>
    Control[] ZoomRows(float s, string name, Func<bool> getKeep, Action<bool> setKeep, Func<double> getZoom, Action<double> setZoom, double zoomHome, string zoomTip)
    {
        var keep = new CheckBox { Text = "Keep Current In-Game Zoom", AutoSize = true, Checked = getKeep(), Margin = new Padding(0, 6, 0, 2) };
        Ui.Tip(keep, $"Ticked: {name} leaves the zoom as it is. Unticked: it sets the zoom below every time.");
        var zoom = new LightSlider { Label = "Zoom", Min = (float)FreeCamSettings.ZoomMin, Max = (float)FreeCamSettings.ZoomMax, Step = 0.05f, Mark = (float)zoomHome, Home = () => (float)zoomHome,
            Format = v => $"{v:0.00} ×", Width = (int)(360 * s), Height = (int)(30 * s), Margin = new Padding(0, 2, 0, 2) };
        zoom.Value = (float)getZoom();
        zoom.ValueChanged += () => { setZoom(Math.Round(zoom.Value * 20) / 20); Changed(); };
        Ui.Tip(zoom, zoomTip);
        void Enable() => zoom.Enabled = !keep.Checked;
        keep.CheckedChanged += (_, _) => { setKeep(keep.Checked); Enable(); Changed(); };
        refreshers.Add(() => { keep.Checked = getKeep(); zoom.Value = (float)getZoom(); Enable(); });
        Enable();
        return [keep, zoom];
    }

    // ------------------------------------------------------------------ default MHO card

    /// <summary>Default MHO: the game's own camera direction, with the game's tilt or a set one, and the height and zoom it starts at.</summary>
    Control DefaultBody(float s)
    {
        var gameTilt = new CheckBox { Text = "Game's Tilt", AutoSize = true, Checked = cfg.DefaultGameTilt, Margin = new Padding(0, 6, 0, 2) };
        Ui.Tip(gameTilt, "Ticked: the camera looks down as the game's own camera does. Unticked: at the tilt below.");
        var tilt = Slider(s, "Tilt", 5, 89, 1, () => cfg.DefaultTilt, v => cfg.DefaultTilt = v, v => $"{v:0}°", Defaults.DefaultTilt, "How far the camera looks down in Default MHO (the direction stays the game's).");
        void EnableTilt() => tilt.Enabled = !gameTilt.Checked;
        gameTilt.CheckedChanged += (_, _) => { cfg.DefaultGameTilt = gameTilt.Checked; EnableTilt(); Changed(); };
        refreshers.Add(() => { gameTilt.Checked = cfg.DefaultGameTilt; EnableTilt(); });
        EnableTilt();
        var height = Slider(s, "Height", -250, 250, 5, () => cfg.DefaultHeight, v => cfg.DefaultHeight = v, v => v == 0 ? "Normal" : v > 0 ? $"+{v:0}" : $"{v:0}", Defaults.DefaultHeight,
            "The camera's height in Default MHO (Normal = the game's own): Freecam's raise / lower (RB + stick doesn't change it in this mode).");
        return Stack(s, [gameTilt, tilt, height, .. ZoomRows(s, "Default MHO", () => cfg.DefaultGameZoom, v => cfg.DefaultGameZoom = v, () => cfg.DefaultZoomX, v => cfg.DefaultZoomX = v, Defaults.DefaultZoomX,
            "The zoom Default MHO starts at, as on Snap Behind: right = closer, 1 × is the game's closest view, 0.5 × twice as far. The game's own zoom step nearest that distance is used, with its angle and height.  (Double-click: back to the default.)")]);
    }

    // ------------------------------------------------------------------ zoom card

    Control ZoomBody(float s) => Stack(s,
        Slider(s, "Extra Steps Closer", 0, 20, 1, () => cfg.ZoomStepsCloser, v => cfg.ZoomStepsCloser = v, v => v == 0 ? "None" : $"{v:0}", Defaults.ZoomStepsCloser, "How many wheel steps you can zoom in past the game's closest step (0 = the game's limit)."),
        Slider(s, "Extra Steps Farther", 0, 20, 1, () => cfg.ZoomStepsFarther, v => cfg.ZoomStepsFarther = v, v => v == 0 ? "None" : $"{v:0}", 10, "How many wheel steps you can zoom out past the game's farthest step (0 = the game's limit)."),
        Slider(s, "Step Size", 5, 40, 1, () => cfg.ZoomStepPercent, v => cfg.ZoomStepPercent = v, v => $"{v:0} %", 15, "How much each extra step changes the camera's distance."),
        zoomInfo);

    void UpdateZoomInfo()
    {
        double f = 1 + cfg.ZoomStepPercent / 100.0;
        zoomInfo.Text = $"Closest: {Math.Pow(f, -cfg.ZoomStepsCloser):0.00} × the game's closest distance  ·  Farthest: {Math.Pow(f, cfg.ZoomStepsFarther):0.0} × its farthest";
    }

    // ------------------------------------------------------------------ helpers

    LightSlider Slider(float s, string label, float min, float max, float step, Func<int> get, Action<int> set, Func<float, string> format, int home, string tip)
    {
        var sl = new LightSlider { Label = label, Min = min, Max = max, Step = step, Mark = home, Home = () => home, Format = format, Width = (int)(360 * s), Height = (int)(30 * s), Margin = new Padding(0, 2, 0, 2) };
        sl.Value = get();
        sl.ValueChanged += () => { set((int)MathF.Round(sl.Value)); UpdateZoomInfo(); Changed(); };
        refreshers.Add(() => sl.Value = get());
        return Ui.Tip(sl, tip + "  (Double-click: back to the default.)");
    }

    static Label Note(float s, string text) => new() { AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(360 * s), 0), Margin = new Padding(0, 6, 0, 0), Text = text };

    CheckBox Check(string text, string tip, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Text = text, AutoSize = true, Checked = get(), Margin = new Padding(0, 6, 0, 0) };
        Ui.Tip(c, tip);
        c.CheckedChanged += (_, _) => { set(c.Checked); Changed(); };
        refreshers.Add(() => c.Checked = get());
        return c;
    }

    static FlowLayoutPanel Stack(float s, params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top };
        f.Controls.AddRange(items);
        // sliders and notes no wider than the card (narrow columns, high display scaling): their value stays visible
        int full = (int)(360 * s);
        f.Resize += (_, _) =>
        {
            int w = Math.Max((int)(160 * s), f.ClientSize.Width - f.Padding.Horizontal - 6);
            foreach (Control c in f.Controls)
            {
                if (c is LightSlider sl) sl.Width = Math.Min(full, w);
                else if (c is Label { AutoSize: true, MaximumSize.Width: > 0 } l) l.MaximumSize = new Size(Math.Min(full, w), 0);
            }
        };
        return f;
    }

    /// <summary>A card; <paramref name="info"/> (the explanation) shows on hovering the ⓘ beside its caption.</summary>
    static Panel Card(string caption, Control content, string? info = null)
    {
        var p = Ui.CardPanel(caption, content);
        if (info != null && p.Controls.OfType<Label>().LastOrDefault(l => l.Text == caption) is Label cap)
        {
            var i = new Label { Text = "ⓘ", AutoSize = true, Font = Ui.Bold(9.5f), Cursor = Cursors.Help, Tag = "subtle", BackColor = Color.Transparent };
            Ui.TipTitled(i, Ui.TitleCase(caption.ToLowerInvariant()), info);
            cap.Controls.Add(i);
            void Place() => i.Location = new Point(TextRenderer.MeasureText(cap.Text, cap.Font).Width + 4, Math.Max(0, (TextRenderer.MeasureText(cap.Text, cap.Font).Height - i.Height) / 2 - 1));
            cap.Resize += (_, _) => Place();
            cap.FontChanged += (_, _) => Place();
            Place();
        }
        p.Dock = DockStyle.Fill; p.AutoSize = true; p.AutoSizeMode = AutoSizeMode.GrowAndShrink; p.Margin = new Padding(6);
        content.Dock = DockStyle.Top;
        return p;
    }

    void Changed()
    {
        cfg.Save();
        footer.Text = status?.Kind == FreeCamBuddy.Kind.Buddy ? "Settings changed: Apply to Game to use them." : "";
    }

    void ResetDefaults()
    {
        var d = new FreeCamSettings();
        foreach (var p in typeof(FreeCamSettings).GetProperties().Where(p => p.CanWrite)) p.SetValue(cfg, p.GetValue(d));
        foreach (var r in refreshers) r();
        UpdateZoomInfo();
        Changed();
    }

    // ------------------------------------------------------------------ status, apply, remove

    public void RefreshStatus()
    {
        string? root = gameRoot();
        if (root == null) { SetStatus(Ui.Warn, "Game Folder Not Set", "Set the game folder first (Settings → Change Game Folder)."); status = null; UpdateButtons(); return; }
        try { status = FreeCamBuddy.Detect(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus(Ui.Warn, "Can't Read MarvelGame.upk", ex.Message); status = null; UpdateButtons(); return; }
        switch (status.Kind)
        {
            case FreeCamBuddy.Kind.Freecam when status.Loader: SetStatus(Ui.Enabled, $"Freecam {status.Version} Installed", "FreeCam Options isn't applied yet: choose your settings, then Apply to Game."); break;
            case FreeCamBuddy.Kind.Buddy when status.Ours && status.Loader: SetStatus(Ui.Enabled, "FreeCam Options Applied", "On top of Freecam. Change settings and Apply to Game again, or Remove to go back to plain Freecam."); break;
            case FreeCamBuddy.Kind.Stock: SetStatus(Ui.Warn, "Freecam Isn't Installed", "FreeCam Options needs Marvel Heroes FreeCam (M.H.F.C.) by MichaelMerc: download it from Nexus (Freecam on Nexus), run its installer with the Full option, then Check Again."); break;
            default: SetStatus(Ui.Warn, status.Kind == FreeCamBuddy.Kind.Freecam ? "Freecam's Loader Is Missing" : "Can't Use This MarvelGame.upk", status.Detail); break;
        }
        UpdateButtons();
    }

    Color statusColor = Ui.Subtle;
    /// <summary>Re-colors the status dot (the theme sets every label's color when the tab is styled).</summary>
    public void ApplyColors() => statusDot.ForeColor = statusColor;

    void SetStatus(Color color, string text, string detail)
    {
        statusColor = color;
        statusDot.ForeColor = color;
        statusText.Text = text;
        statusDetail.Text = detail;
    }

    void UpdateButtons()
    {
        applyButton.Enabled = !busy && status is { CanApply: true };
        removeButton.Enabled = !busy && (status is { Kind: FreeCamBuddy.Kind.Buddy, Ours: true } || gameRoot() is string r && HudLabels.IsOurs(r));
    }

    async void Apply()
    {
        if (gameRoot() is not string root || busy) return;
        if (Process.GetProcessesByName("MarvelHeroesOmega").Length > 0) { Dialog.Show(FindForm(), "Close the game first: the game's files can only be changed while it isn't running.", "The Game Is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        await Run("Applying", log => FreeCamBuddy.Apply(root, cfg, modsNamingIt(), dryRun: false, log), "FreeCam Options Applied", "Start the game: your settings are in. In game: the keys above, and the mouse wheel past the usual zoom.");
    }

    async void Remove()
    {
        if (gameRoot() is not string root || busy) return;
        if (Process.GetProcessesByName("MarvelHeroesOmega").Length > 0) { Dialog.Show(FindForm(), "Close the game first: the game's files can only be changed while it isn't running.", "The Game Is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (Dialog.Show(FindForm(), "Put Freecam's own MarvelGame.upk back, show the game's overhead labels again and take FreeCam Options's keys out of the game's input settings?\n\nYour settings on this tab are kept.", "Remove FreeCam Options", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        await Run("Removing", log => FreeCamBuddy.Remove(root, dryRun: false, log), "FreeCam Options Removed", "Freecam is as its installer left it.");
    }

    async Task Run(string doing, Func<List<string>, bool> work, string doneHeading, string doneText)
    {
        busy = true; UpdateButtons();
        footer.Text = doing + "…";
        var log = new List<string>();
        bool ok;
        try { ok = await Task.Run(() => work(log)); }
        catch (Exception ex) { log.Add(ex.Message); ok = false; }
        busy = false;
        footer.Text = "";
        RefreshStatus();
        foreach (var r in refreshers) r();
        if (ok) Dialog.ShowLog(FindForm(), doneHeading, doneText + "\n\n" + string.Join("\n", log.Select(l => "• " + l)), Dialog.Tone.Good);
        else Dialog.ShowLog(FindForm(), "Not Applied", string.Join("\n", log.Select(l => "• " + l)), Dialog.Tone.Bad);
    }
}
