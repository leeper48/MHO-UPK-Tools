namespace MhoExtendedModManager;

/// <summary>
/// Every keyboard shortcut and 3D-view mouse binding of the app, with the user's own (Kurt, 2026-10-25: Settings → Keyboard
/// Shortcuts, changeable, conflicts shown). A command has an area (where it works), a default and the user's binding
/// (settings.json "Shortcuts": id → binding, only the changed ones). A binding is one or more, separated by ", ":
/// keys ("Ctrl+Shift+Z", "F11", "Delete"), mouse ("Shift+Middle Drag", "Right Drag", "Ctrl+Click", "Double Click",
/// "Shift+Wheel"), or modifier keys held while painting ("Shift", "Ctrl"). Dialogs' Enter / Esc are fixed and not listed.
/// </summary>
static class Shortcuts
{
    /// <summary>Where a command works; two areas that can be active together can't share a binding (see <see cref="Overlap"/>).</summary>
    public enum Area { App, ModsTab, Editor, View3D, ModelTab, Painting, IconCreator }

    public enum Kind { Key, MouseDrag, MouseClick, Wheel, Held }

    public sealed record Command(string Id, Area Area, Kind Kind, string Name, string Default, string Description);

    public static readonly IReadOnlyList<Command> All =
    [
        new("app.help", Area.App, Kind.Key, "Open the Manual", "F1", "The manual (Help)."),
        new("mods.apply", Area.ModsTab, Kind.Key, "Apply Changes", "Ctrl+Enter", "Apply Changes on the Mods tab."),
        new("mods.remove", Area.ModsTab, Kind.Key, "Remove Mod", "Delete", "Remove the selected mod (asks first)."),
        new("mods.up", Area.ModsTab, Kind.Key, "Move Up", "Ctrl+Up", "The selected mod (or the marked ones) one place higher in priority."),
        new("mods.down", Area.ModsTab, Kind.Key, "Move Down", "Ctrl+Down", "One place lower in priority."),
        new("mods.top", Area.ModsTab, Kind.Key, "Move to the Top", "Ctrl+Home", "To the top of the priority list."),
        new("mods.bottom", Area.ModsTab, Kind.Key, "Move to the Bottom", "Ctrl+End", "To the bottom of the priority list."),
        new("mods.undo", Area.ModsTab, Kind.Key, "Undo", "Ctrl+Z", "Undo a change to the mod list."),
        new("mods.redo", Area.ModsTab, Kind.Key, "Redo", "Ctrl+Y, Ctrl+Shift+Z", "Redo a change to the mod list."),
        new("editor.save", Area.Editor, Kind.Key, "Save and Keep Editing", "Ctrl+S", "Save the mod and stay in the Editor."),
        new("editor.saveclose", Area.Editor, Kind.Key, "Save and Go Back", "Ctrl+Enter", "Save the mod and go back to the mod list."),
        new("view.play", Area.View3D, Kind.Key, "Play / Pause", "P", "Plays or pauses the animation in a 3D view."),
        new("view.pause", Area.View3D, Kind.Key, "Pause / Leave Full Screen", "Escape", "Pauses the animation; in full screen, the next press comes back."),
        new("view.quad", Area.View3D, Kind.Key, "Quad View", "Shift+Q", "This camera plus Top, Front and Side."),
        new("view.full", Area.View3D, Kind.Key, "Full Screen", "F11", "The preview on its own, full screen."),
        new("view.turn", Area.View3D, Kind.MouseDrag, "Turn the View", "Left Drag", "Drag to turn the camera around the model."),
        new("view.move", Area.View3D, Kind.MouseDrag, "Move the View", "Right Drag, Middle Drag", "Drag to move the camera sideways and up / down."),
        new("view.zoom", Area.View3D, Kind.Wheel, "Zoom", "Wheel", "Zoom in and out (8 % a notch)."),
        new("view.zoomfine", Area.View3D, Kind.Wheel, "Zoom Finely", "Shift+Wheel", "Zoom in smaller steps (2 % a notch)."),
        new("view.frame", Area.View3D, Kind.MouseClick, "Frame the Model", "Double Click", "Frames the whole model again."),
        new("view.gizmoback", Area.View3D, Kind.Held, "Gizmo: From the Other Side", "Shift", "Held while clicking an axis ball of the gizmo: look along that axis from the other side."),
        new("model.bones", Area.ModelTab, Kind.Key, "Bones", "B", "The skeleton over the model."),
        new("model.weights", Area.ModelTab, Kind.Key, "Weights", "W", "Off → the weight colours over the model at 50 % → the weights alone."),
        new("model.paint", Area.ModelTab, Kind.Key, "Paint On / Off", "Shift+P", "Weight painting (Bone Map tab)."),
        new("model.boneup", Area.ModelTab, Kind.Key, "Bone Above", "[", "The parent of the picked bone (skin-carrying bones only); while painting, the brush paints it."),
        new("model.bonedown", Area.ModelTab, Kind.Key, "Bone Below", "]", "A child of the picked bone (skin-carrying bones only); while painting, the brush paints it."),
        new("model.pickbone", Area.ModelTab, Kind.MouseClick, "Pick a Bone / Material", "Ctrl+Click", "Picks the bone under the mouse in the Bone Map tab (on the Materials tab: the material)."),
        new("model.undo", Area.ModelTab, Kind.Key, "Undo", "Ctrl+Z", "Undo in the Model tab (parts, bone map, materials, painting …)."),
        new("model.redo", Area.ModelTab, Kind.Key, "Redo", "Ctrl+Y, Ctrl+Shift+Z", "Redo in the Model tab."),
        new("paint.paint", Area.Painting, Kind.MouseDrag, "Paint", "Left Drag", "Paints the selected bone's weight."),
        new("paint.turn", Area.Painting, Kind.MouseDrag, "Turn the View (Painting)", "Middle Drag", "Turns the camera while painting."),
        new("paint.move", Area.Painting, Kind.MouseDrag, "Move the View (Painting)", "Shift+Middle Drag, Right Drag", "Moves the camera while painting."),
        new("paint.blur", Area.Painting, Kind.Held, "Blur (Held)", "Shift", "Held while painting: any brush blurs."),
        new("paint.otherway", Area.Painting, Kind.Held, "The Other Way (Held)", "Ctrl", "Held while painting: Draw toward 1 − Weight, Add and Subtract swapped."),
        new("paint.pickbone", Area.Painting, Kind.MouseClick, "Pick a Bone (Painting)", "Ctrl+Shift+Click", "Picks the bone under the mouse while painting."),
        new("paint.radius", Area.Painting, Kind.Key, "Brush Radius", "F", "Move the mouse to change the radius; click to keep, Esc to put it back."),
        new("paint.strength", Area.Painting, Kind.Key, "Brush Strength", "Shift+F", "Move the mouse to change the strength; click to keep, Esc to put it back."),
        new("icon.overlay", Area.IconCreator, Kind.Key, "Original as Overlay", "O", "Create from 3D: the original icon over the view."),
    ];

    static Dictionary<string, string> user = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, List<string>> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's bindings (settings.json Shortcuts), read at start and after the Shortcuts window saves.</summary>
    public static void Load(IReadOnlyDictionary<string, string>? fromSettings)
    {
        user = fromSettings == null ? new(StringComparer.OrdinalIgnoreCase) : new(fromSettings, StringComparer.OrdinalIgnoreCase);
        cache = new(StringComparer.OrdinalIgnoreCase);
    }

    public static Command? Find(string id) => All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A button's own shortcut (right-click → Assign Shortcut): "button:&lt;window&gt;/&lt;name&gt;", a key, everywhere.</summary>
    public static Command ButtonCommand(string id, string name) => new(id, Area.App, Kind.Key, "Button: " + name, "", "Presses the button " + name + " (when it's showing and can be pressed).");

    /// <summary>The buttons given a shortcut (id, binding).</summary>
    public static List<(string Id, string Binding)> ButtonBindings() => [.. user.Where(kv => kv.Key.StartsWith("button:", StringComparison.OrdinalIgnoreCase) && kv.Value.Length > 0).Select(kv => (kv.Key, kv.Value))];

    /// <summary>Every command, the buttons' own shortcuts last.</summary>
    public static IEnumerable<Command> Everything() =>
        All.Concat(ButtonBindings().Select(b => ButtonCommand(b.Id, b.Id[(b.Id.IndexOf('/') + 1)..] + " (" + b.Id["button:".Length..b.Id.IndexOf('/')].Replace("Form", "").Replace("View", "").Replace("Page", "") + ")")));

    /// <summary>The binding text of a command (the user's, else the default).</summary>
    public static string Text(string id) => user.TryGetValue(id, out var t) ? t : Find(id)?.Default ?? "";

    static List<string> Bindings(string id)
    {
        if (!cache.TryGetValue(id, out var l))
            cache[id] = l = [.. Text(id).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize)];
        return l;
    }

    // --- keys -----------------------------------------------------------------------------------------------------------------
    /// <summary>A key with modifiers as a binding ("Ctrl+Shift+Z"); modifier keys alone give "".</summary>
    public static string KeyText(Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu or Keys.None) return "";
        return Mods(keyData) + KeyName(code);
    }

    static string Mods(Keys k) => ((k & Keys.Control) != 0 ? "Ctrl+" : "") + ((k & Keys.Alt) != 0 ? "Alt+" : "") + ((k & Keys.Shift) != 0 ? "Shift+" : "");

    static string KeyName(Keys code) => code switch
    {
        Keys.Return => "Enter", Keys.Escape => "Escape", Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]", Keys.Delete => "Delete", Keys.Next => "PageDown", Keys.Prior => "PageUp",
        >= Keys.D0 and <= Keys.D9 => ((int)(code - Keys.D0)).ToString(),
        _ => code.ToString(),
    };

    /// <summary>A binding written the one way: modifiers in the order Ctrl, Alt, Shift; Esc = Escape, Del = Delete …</summary>
    public static string Normalize(string b)
    {
        var parts = b.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        bool ctrl = parts.RemoveAll(p => p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) > 0;
        bool alt = parts.RemoveAll(p => p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) > 0;
        bool shift = parts.RemoveAll(p => p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) > 0;
        string rest = string.Join("+", parts.Select(p => p.ToLowerInvariant() switch { "esc" => "Escape", "del" => "Delete", "return" => "Enter", _ => p }));
        return (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + rest;
    }

    /// <summary>Whether <paramref name="keyData"/> (ProcessCmdKey's, or a KeyDown's KeyData) is a binding of <paramref name="id"/>.</summary>
    public static bool Is(string id, Keys keyData)
    {
        string t = KeyText(keyData);
        return t.Length > 0 && Bindings(id).Contains(t, StringComparer.OrdinalIgnoreCase);
    }

    // --- mouse ----------------------------------------------------------------------------------------------------------------
    static string ButtonName(MouseButtons b) => b switch { MouseButtons.Left => "Left", MouseButtons.Right => "Right", MouseButtons.Middle => "Middle", MouseButtons.XButton1 => "Back", MouseButtons.XButton2 => "Forward", _ => "" };

    /// <summary>A drag binding of <paramref name="id"/> starting with this button and these modifier keys.</summary>
    public static bool Drag(string id, MouseButtons button, Keys modifiers) =>
        Bindings(id).Contains(Mods(modifiers) + ButtonName(button) + " Drag", StringComparer.OrdinalIgnoreCase);

    /// <summary>A click binding ("Ctrl+Click" = the left button; "Middle Click" …) of <paramref name="id"/>.</summary>
    public static bool Click(string id, MouseButtons button, Keys modifiers)
    {
        string m = Mods(modifiers);
        return Bindings(id).Any(b => b.Equals(m + (button == MouseButtons.Left ? "Click" : ButtonName(button) + " Click"), StringComparison.OrdinalIgnoreCase)
            || button == MouseButtons.Left && b.Equals(m + "Left Click", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A double-click binding of <paramref name="id"/>.</summary>
    public static bool DoubleClick(string id, Keys modifiers) => Bindings(id).Contains(Mods(modifiers) + "Double Click", StringComparer.OrdinalIgnoreCase);

    /// <summary>A wheel binding of <paramref name="id"/> with these modifier keys.</summary>
    public static bool Wheel(string id, Keys modifiers) => Bindings(id).Contains(Mods(modifiers) + "Wheel", StringComparer.OrdinalIgnoreCase);

    /// <summary>The modifier keys of a "held" command (all its bindings).</summary>
    public static Keys HeldKeys(string id) => Bindings(id).Aggregate(Keys.None, (k, b) => k
        | (b.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) ? Keys.Control : 0) | (b.Contains("Shift", StringComparison.OrdinalIgnoreCase) ? Keys.Shift : 0) | (b.Contains("Alt", StringComparison.OrdinalIgnoreCase) ? Keys.Alt : 0));

    /// <summary>Whether the modifier keys of a "held" command are all down in <paramref name="modifiers"/>.</summary>
    public static bool Held(string id, Keys modifiers) => Bindings(id).Any(b =>
    {
        var want = (b.Contains("Ctrl", StringComparison.OrdinalIgnoreCase) ? Keys.Control : 0) | (b.Contains("Shift", StringComparison.OrdinalIgnoreCase) ? Keys.Shift : 0) | (b.Contains("Alt", StringComparison.OrdinalIgnoreCase) ? Keys.Alt : 0);
        return want != 0 && (modifiers & want) == want;
    });

    /// <summary>The modifier keys of every mouse binding that starts with a click or drag of the left button while painting
    /// (Pick a Bone (Painting)): a stroke doesn't start then.</summary>
    public static bool PaintSuppressed(MouseButtons button, Keys modifiers) => Click("paint.pickbone", button, modifiers);

    // --- tooltips ---------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// A tooltip with the user's keys in place of the default ones it names: "(Ctrl+Z)", "(P; …", "(F1)", "Key: W", "Ctrl+click"
    /// … With <paramref name="id"/> (the control's own command) only that command's; otherwise every changed command whose
    /// default no other command has (Undo's Ctrl+Z is two commands' default: a tooltip can't tell which it means).
    /// </summary>
    public static string Rewrite(string text, string? id)
    {
        if (user.Count == 0 || text.Length == 0) return text;
        IEnumerable<Command> which = id != null && Find(id) is { } own ? [own]
            : All.Where(c => user.ContainsKey(c.Id) && All.Count(o => Normalize(o.Default) == Normalize(c.Default)) == 1);
        // longest defaults first (Shift+F before F), each only where it stands alone ("F" not inside "Shift+F")
        foreach (var c in which.OrderByDescending(c => c.Default.Length))
        {
            if (!user.TryGetValue(c.Id, out var now)) continue;
            string shown = now.Length > 0 ? now.Replace(", ", " / ") : "no shortcut";
            foreach (var d in c.Default.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderByDescending(d => d.Length))
                text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![A-Za-z0-9+])" + System.Text.RegularExpressions.Regex.Escape(d) + @"(?=\)|;|,|: |\.|\s\(|$|\+[cC]lick)", shown.Replace("$", "$$"));
        }
        return text;
    }

    // --- conflicts --------------------------------------------------------------------------------------------------------------
    /// <summary>Whether two areas can be active at once (their bindings would clash).</summary>
    public static bool Overlap(Area a, Area b)
    {
        if (a == b || a == Area.App || b == Area.App) return true;
        static bool Pair(Area x, Area y, Area p, Area q) => x == p && y == q || x == q && y == p;
        return Pair(a, b, Area.ModsTab, Area.View3D) || Pair(a, b, Area.Editor, Area.View3D) || Pair(a, b, Area.Editor, Area.ModelTab)
            || Pair(a, b, Area.Editor, Area.IconCreator) || Pair(a, b, Area.ModelTab, Area.View3D) || Pair(a, b, Area.ModelTab, Area.Painting)
            || Pair(a, b, Area.IconCreator, Area.View3D);
    }

    /// <summary>Painting replaces the view's own mouse (its drags) and the Model tab's Ctrl+click while it's on: those don't clash.</summary>
    static bool Replaces(Command a, Command b) =>
        a.Area == Area.Painting && b.Area is Area.View3D or Area.ModelTab && a.Kind != Kind.Key && b.Kind != Kind.Key
        || b.Area == Area.Painting && a.Area is Area.View3D or Area.ModelTab && a.Kind != Kind.Key && b.Kind != Kind.Key;

    /// <summary>Kinds whose bindings can clash: keys with keys, mouse with mouse (a held modifier clashes with nothing but other
    /// held ones in its area).</summary>
    static bool SameFamily(Kind a, Kind b) => a == Kind.Key ? b == Kind.Key : a == Kind.Held ? b == Kind.Held : b is not (Kind.Key or Kind.Held);

    /// <summary>Every other command sharing a binding with <paramref name="c"/> where both can be active (with the bindings in
    /// <paramref name="bindings"/>: id → text, the window's unsaved ones).</summary>
    public static List<(Command Other, string Binding)> Conflicts(Command c, Func<string, string> bindings)
    {
        var mine = bindings(c.Id).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize).ToList();
        var found = new List<(Command, string)>();
        foreach (var o in Everything())
        {
            if (o.Id == c.Id || !Overlap(c.Area, o.Area) || !SameFamily(c.Kind, o.Kind) || Replaces(c, o)) continue;
            // the same action in two areas (Undo on the Mods tab and in the Model tab) never runs at once: not a clash
            if (o.Name == c.Name && o.Area != c.Area) continue;
            if (c.Kind == Kind.Held && o.Area != c.Area) continue;
            foreach (var b in bindings(o.Id).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize))
                if (mine.Contains(b, StringComparer.OrdinalIgnoreCase)) found.Add((o, b));
        }
        return found;
    }

    public static string AreaName(Area a) => a switch
    {
        Area.App => "Everywhere", Area.ModsTab => "Mods Tab", Area.Editor => "Editor", Area.View3D => "3D Views",
        Area.ModelTab => "Model Tab", Area.Painting => "Weight Painting", Area.IconCreator => "Create from 3D", _ => a.ToString(),
    };
}
