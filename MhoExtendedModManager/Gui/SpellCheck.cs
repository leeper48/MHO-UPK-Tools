using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Spell checking in the app's text boxes (Kurt, 2026-11-17): the Windows spell checker (ISpellChecker, Windows 8 and later; the
/// one Edge and the Windows text boxes use, with the user's own dictionary), so nothing is shipped. Misspelled words get a red
/// wavy line; a right-click on one offers its suggestions, Add to Dictionary and Ignore (else the box's own menu). Words with
/// digits or game markup (#keyword#, {0}, &lt;tags&gt;), links, and the game's character and team names are left alone.
/// Off in Settings → Spell Check. Nothing happens on a Windows without a spell checker for English.
/// </summary>
static class SpellCheck
{
    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig] int Next(out ISpellingError? value);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
        int CorrectiveAction { get; }   // 0 none, 1 get suggestions, 2 replace, 3 delete (a repeated word)
        string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    }

    /// <summary>On (Settings → Spell Check).</summary>
    public static bool Enabled { get; set; } = true;
    public static event Action? EnabledChanged;
    public static void SetEnabled(bool on) { if (Enabled == on) return; Enabled = on; EnabledChanged?.Invoke(); }

    static ISpellChecker? checker;
    static bool tried;

    static ISpellChecker? Checker()
    {
        if (tried) return checker;
        tried = true;
        try
        {
            if (Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")) is { } t && Activator.CreateInstance(t) is ISpellCheckerFactory f)
            {
                string lang = f.IsSupported("en-US") ? "en-US" : f.IsSupported("en-GB") ? "en-GB" : "";
                if (lang.Length > 0) checker = f.CreateSpellChecker(lang);
            }
            // the game's character and team names, and words of the app's own (this session only)
            if (checker != null)
                foreach (string w in AutoTags.NameWords.Concat(["MHO", "MEMM", "Nexus", "MHModManager", "upk", "UPK", "DDS", "PNG", "FBX", "Blender", "GIMP", "MFF", "costume", "team-up", "Teamup", "Teamups", "NPC", "NPCs", "emote", "emotes", "Mixamo"]))
                    if (w.Length > 1) try { checker.Ignore(w); } catch (COMException) { }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or TypeLoadException) { checker = null; }
        return checker;
    }

    /// <summary>A misspelled range in the text: start, length, and the replacement for a repeated word ("" = delete).</summary>
    readonly record struct Miss(int Start, int Length, int Action, string Replacement);

    static List<Miss> Check(string text)
    {
        var res = new List<Miss>();
        if (!Enabled || text.Length == 0 || text.Length > 60000 || Checker() is not { } c) return res;
        try
        {
            var e = c.Check(text);
            while (e.Next(out var err) == 0 && err != null)
            {
                int s = (int)err.StartIndex, n = (int)err.Length;
                int action = err.CorrectiveAction;
                string replacement = action == 2 ? err.Replacement ?? "" : "";
                Marshal.ReleaseComObject(err);
                if (s < 0 || n <= 0 || s + n > text.Length || Skip(text, s, n)) continue;
                res.Add(new Miss(s, n, action, replacement));
            }
        }
        catch (COMException) { }
        return res;
    }

    /// <summary>Not a word to check: digits, game markup (#keyword#, {0}, &lt;b&gt;, [b]), a link, a path, a file name.</summary>
    static bool Skip(string text, int s, int n)
    {
        string w = text.Substring(s, n);
        if (w.Any(char.IsDigit) || w.Contains('_')) return true;
        char before = s > 0 ? text[s - 1] : ' ', after = s + n < text.Length ? text[s + n] : ' ';
        if (before is '#' or '{' or '<' or '[' or '\\' or '/' or '.' or '@' || after is '#' or '}' or '>' or ']' or '\\' or '/') return true;
        // inside a link or a path (http…, www., C:\…)
        int ls = text.LastIndexOfAny([' ', '\n', '\r', '\t', '('], Math.Max(0, s - 1)) + 1;
        int le = text.IndexOfAny([' ', '\n', '\r', '\t', ')'], s); if (le < 0) le = text.Length;
        string token = text[ls..le];
        return token.Contains("://") || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || token.Contains(":\\") || token.Contains('\\');
    }

    /// <summary>Test (--spell-test): each misspelled word with its suggestions.</summary>
    internal static string Test(string text)
    {
        if (Checker() is null) return "no Windows spell checker for English";
        var lines = Check(text).Select(m => $"{text.Substring(m.Start, m.Length)} (action {m.Action}): {string.Join(", ", m.Action == 3 ? ["(delete the repeated word)"] : Suggest(text.Substring(m.Start, m.Length)))}").ToList();
        return lines.Count == 0 ? "no misspelled words" : string.Join(Environment.NewLine, lines);
    }

    static List<string> Suggest(string word)
    {
        var list = new List<string>();
        if (Checker() is not { } c) return list;
        try
        {
            var e = c.Suggest(word);
            var one = new string[1];
            while (list.Count < 6 && e.Next(1, one, IntPtr.Zero) == 0 && one[0] != null) list.Add(one[0]);
        }
        catch (COMException) { }
        return list;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, Hook> hooks = new();

    /// <summary>Spell checks <paramref name="box"/> (idempotent; follows a recreated handle).</summary>
    public static T Attach<T>(T box) where T : TextBox
    {
        if (hooks.TryGetValue(box, out _)) return box;
        hooks.Add(box, new Hook(box));
        return box;
    }

    /// <summary>Watches one box: checks its text a moment after it changes, draws the lines after the box paints, and puts the
    /// suggestions in front of its right-click menu over a misspelled word.</summary>
    sealed class Hook : NativeWindow
    {
        readonly TextBox box;
        readonly System.Windows.Forms.Timer delay = new() { Interval = 350 };
        List<Miss> misses = [];

        public Hook(TextBox box)
        {
            this.box = box;
            if (box.IsHandleCreated) AssignHandle(box.Handle);
            box.HandleCreated += (_, _) => { if (Handle != box.Handle) { ReleaseHandle(); AssignHandle(box.Handle); } Recheck(); };
            box.HandleDestroyed += (_, _) => ReleaseHandle();
            box.TextChanged += (_, _) => { delay.Stop(); delay.Start(); };
            box.Disposed += (_, _) => { delay.Dispose(); EnabledChanged -= Recheck; };
            delay.Tick += (_, _) => { delay.Stop(); Recheck(); };
            EnabledChanged += Recheck;
            Recheck();
        }

        void Recheck()
        {
            if (box.IsDisposed) return;
            var now = box.ReadOnly ? [] : Check(box.Text);
            if (now.Count == 0 && misses.Count == 0) return;
            misses = now;
            if (box.IsHandleCreated) box.Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x007B && misses.Count > 0 && ShowMenu(m.LParam)) return;   // WM_CONTEXTMENU over a misspelled word
            base.WndProc(ref m);
            if (m.Msg == 0x000F && misses.Count > 0) DrawLines();   // WM_PAINT
        }

        void DrawLines()
        {
            try
            {
                using var g = Graphics.FromHwnd(box.Handle);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.FromArgb(235, 85, 85), 1f);
                var area = box.ClientRectangle;
                int lineH = box.Font.Height;
                string text = box.Text;
                foreach (var miss in misses)
                {
                    if (miss.Start + miss.Length > text.Length) continue;
                    var a = box.GetPositionFromCharIndex(miss.Start);
                    int last = miss.Start + miss.Length - 1;
                    var b = box.GetPositionFromCharIndex(last);
                    if (b.Y != a.Y) continue;   // (wrapped: left alone)
                    int x2 = b.X + TextRenderer.MeasureText(g, text[last].ToString(), box.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    float y = a.Y + lineH - 2;
                    if (y < area.Top || y > area.Bottom || x2 < area.Left || a.X > area.Right) continue;
                    var pts = new List<PointF>();
                    for (float x = a.X; x <= x2; x += 2) pts.Add(new PointF(x, y + ((int)((x - a.X) / 2) % 2 == 0 ? 0 : 2)));
                    if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException) { }
        }

        bool ShowMenu(IntPtr lParam)
        {
            int lp = unchecked((int)lParam.ToInt64());
            var screen = lp == -1 ? box.PointToScreen(box.GetPositionFromCharIndex(box.SelectionStart)) : new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            int at = box.GetCharIndexFromPosition(box.PointToClient(screen));
            var hit = misses.FirstOrDefault(x => at >= x.Start && at < x.Start + x.Length);
            if (hit.Length == 0) return false;
            string word = box.Text.Substring(hit.Start, hit.Length);
            var menu = new ContextMenuStrip();
            void Replace(string with)
            {
                box.Focus();
                box.Select(hit.Start, hit.Length);
                SendMessage(box.Handle, 0x00C2, 1, with);   // EM_REPLACESEL, can undo (Ctrl+Z)
                Recheck();
            }
            if (hit.Action == 3)
            {
                // a repeated word ("the the"): deleted with the space before it
                menu.Items.Add(new ToolStripMenuItem("Delete Repeated Word", null, (_, _) =>
                {
                    int s0 = hit.Start > 0 && box.Text[hit.Start - 1] == ' ' ? hit.Start - 1 : hit.Start;
                    box.Focus(); box.Select(s0, hit.Start + hit.Length - s0);
                    SendMessage(box.Handle, 0x00C2, 1, "");
                    Recheck();
                }) { Font = new Font(menu.Font, FontStyle.Bold) });
                menu.Items.Add(new ToolStripSeparator());
            }
            var suggestions = hit.Action == 3 ? [] : hit.Replacement.Length > 0 ? [hit.Replacement] : Suggest(word);
            foreach (string s in suggestions) menu.Items.Add(new ToolStripMenuItem(s, null, (_, _) => Replace(s)) { Font = new Font(menu.Font, FontStyle.Bold) });
            if (suggestions.Count == 0 && hit.Action != 3) menu.Items.Add(new ToolStripMenuItem("No Suggestions") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Add to Dictionary", null, (_, _) => { try { checker?.Add(word); } catch (COMException) { } Recheck(); }) { ToolTipText = $"\"{word}\" is never marked again (your Windows dictionary)." });
            menu.Items.Add(new ToolStripMenuItem("Ignore", null, (_, _) => { try { checker?.Ignore(word); } catch (COMException) { } Recheck(); }) { ToolTipText = $"\"{word}\" isn't marked again until the app closes." });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Cut", null, (_, _) => box.Cut()) { Enabled = box.SelectionLength > 0 && !box.ReadOnly });
            menu.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) => box.Copy()) { Enabled = box.SelectionLength > 0 });
            menu.Items.Add(new ToolStripMenuItem("Paste", null, (_, _) => box.Paste()) { Enabled = !box.ReadOnly && Clipboard.ContainsText() });
            menu.Closed += (_, _) => box.BeginInvoke(menu.Dispose);
            Ui.ShowAt(menu, screen);
            return true;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, string lParam);
    }
}
