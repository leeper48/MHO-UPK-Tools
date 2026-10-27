using System.Runtime.InteropServices;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Every file and folder picker opens in the view set in Settings → File Pickers (a user, 2026-10-27: image pickers kept going
/// back to Details): one for packages and other files (default Details), one for pickers whose file types are images (default
/// Last Used: the view the user left the last image picker in). Details, as before: every picker opened in Details view with useful columns (Kurt, 2026-10-17: the packages picker opened in
/// Large Icons): Name and Folder (the file's location) wide, Date Modified, Type and Size; no Tags or Rating. Windows' dialogs
/// have no setting for it, so a timer on the UI thread (it runs while a dialog is open: the dialog's own loop dispatches its
/// messages) looks for an open file dialog of this thread and, each time its folder view is new (a dialog opening, or
/// another folder opened in it), sets the view through the dialog's shell browser (WM_GETISHELLBROWSER → IShellView →
/// IFolderView / IColumnManager). Anything that fails is left as Windows shows it.
/// </summary>
static class FileDialogView
{
    static System.Windows.Forms.Timer? timer;
    static readonly HashSet<IntPtr> done = [];
    /// <summary>Open folder views: whether the picker lists images, and the view last read from it (for Last Used).</summary>
    static readonly Dictionary<IntPtr, (bool Images, string? Seen)> open = [];
    static bool dirty;

    /// <summary>The views offered in Settings → File Pickers: name → (mode, icon size; 0 = the mode's own).</summary>
    public static readonly (string Name, uint Mode, int Size)[] Views =
    [
        ("Details", FVM_DETAILS, 0), ("List", 3, 0), ("Small Icons", 2, 16), ("Medium Icons", 1, 48), ("Large Icons", 1, 96),
        ("Extra Large Icons", 1, 256), ("Tiles", 6, 48), ("Content", 8, 32),
    ];
    public const string LastUsed = "Last Used";

    public static void Start()
    {
        // (made on the first idle, once the message loop runs: a timer makes a window, and none may exist before the app's
        // SetCompatibleTextRenderingDefault; started here directly, 0.37.295 didn't open)
        void First(object? s, EventArgs e)
        {
            Application.Idle -= First;
            if (timer != null) return;
            timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (_, _) => Check();
            timer.Start();
        }
        Application.Idle += First;
    }

    static void Check()
    {
        var seen = new HashSet<IntPtr>();
        EnumThreadWindows(GetCurrentThreadId(), (w, _) =>
        {
            if (ClassOf(w) == "#32770" && IsWindowVisible(w))
            {
                IntPtr view = FindChild(w, "SHELLDLL_DefView");
                if (view != IntPtr.Zero)
                {
                    seen.Add(view);
                    if (done.Add(view)) { bool images = ListsImages(w); Apply(w, images); open[view] = (images, null); }
                    else if (open.TryGetValue(view, out var o) && ReadView(w) is { } now && now != o.Seen)
                    {
                        // the user changed the view (or it was just set): remembered for Last Used
                        open[view] = (o.Images, now);
                        Trace($"seen {now} images={o.Images}");
                        var st = MhoExtendedModManager.Model.Settings.App;
                        if (st != null)
                        {
                            if (o.Images && st.FileViewLastImages != now) { st.FileViewLastImages = now; dirty = true; }
                            if (!o.Images && st.FileViewLastPackages != now) { st.FileViewLastPackages = now; dirty = true; }
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);
        done.IntersectWith(seen);   // (closed dialogs and folders left: forgotten)
        foreach (var k in open.Keys.Where(k => !seen.Contains(k)).ToList()) open.Remove(k);
        if (dirty && open.Count == 0) { dirty = false; MhoExtendedModManager.Model.Settings.Change(_ => { }); }   // (saved once the pickers close)
    }

    /// <summary>Whether a picker's file types are images (its file-type box names *.png, *.dds …; a folder picker has none).</summary>
    static readonly string? trace = Environment.GetEnvironmentVariable("MHO_FDV_TRACE");
    static void Trace(string t) { if (trace != null) File.AppendAllText(trace, $"{DateTime.Now:HH:mm:ss.fff} {t}" + Environment.NewLine); }

    static bool ListsImages(IntPtr dialog)
    {
        bool images = false;
        EnumChildWindows(dialog, (w, _) =>
        {
            if (ClassOf(w) != "ComboBox") return true;
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(w, sb, sb.Capacity);
            string t = sb.ToString().ToLowerInvariant();
            if (!t.Contains("*.")) return true;   // (the file name box)
            images = new[] { "*.png", "*.dds", "*.jpg", "*.jpeg", "*.bmp", "*.tga", "*.gif" }.Any(t.Contains);
            return false;
        }, IntPtr.Zero);
        return images;
    }

    /// <summary>The view a picker of this kind should open in: (mode, icon size), or null = as Windows shows it.</summary>
    static (uint Mode, int Size)? Wanted(bool images)
    {
        var st = MhoExtendedModManager.Model.Settings.App;
        string pick = (images ? st?.FileViewImages : st?.FileViewPackages) ?? (images ? LastUsed : "Details");
        if (pick == LastUsed)
        {
            string? last = images ? st?.FileViewLastImages : st?.FileViewLastPackages;
            if (last?.Split(':') is [var m, var z] && uint.TryParse(m, out uint mode) && int.TryParse(z, out int size)) return (mode, size);
            return images ? null : (FVM_DETAILS, 0);
        }
        foreach (var v in Views) if (v.Name == pick) return (v.Mode, v.Size);
        return (FVM_DETAILS, 0);
    }

    /// <summary>A picker's current view as "mode:icon size", or null.</summary>
    static string? ReadView(IntPtr dialog)
    {
        IntPtr view = ActiveView(dialog);
        if (view == IntPtr.Zero) return null;
        try
        {
            if (Query(view, IID_IFolderView2) is not { } fv) return null;
            try { return Call<GetModeSizeFn>(fv, 36)(fv, out uint mode, out int size) == 0 ? $"{mode}:{size}" : null; }
            finally { Marshal.Release(fv); }
        }
        finally { Marshal.Release(view); }
    }

    static void Apply(IntPtr dialog, bool images)
    {
        Trace($"apply images={images} want={Wanted(images)}");
        if (Wanted(images) is not { } want) return;
        // (raw COM calls: .NET's wrappers for these shell interfaces fail with "type library is not registered")
        IntPtr view = ActiveView(dialog);
        if (view == IntPtr.Zero) return;
        try
        {
            if (want.Size > 0 && Query(view, IID_IFolderView2) is { } fv2) { try { Call<SetModeSizeFn>(fv2, 35)(fv2, want.Mode, want.Size); } finally { Marshal.Release(fv2); } }
            else if (Query(view, IID_IFolderView) is { } fv) { try { Call<SetModeFn>(fv, 4)(fv, want.Mode); } finally { Marshal.Release(fv); } }
            if (want.Mode == FVM_DETAILS && Query(view, IID_IColumnManager) is { } cm)
                try
                {
                    PROPERTYKEY[] cols = [Name, DateModified, ItemType, Size, FolderPath];
                    Call<SetColumnsFn>(cm, 7)(cm, cols, (uint)cols.Length);
                    float sc = GetDpiForWindow(dialog) / 96f;
                    foreach (var (key, width) in new[] { (Name, 380), (DateModified, 150), (ItemType, 110), (Size, 80), (FolderPath, 360) })
                    {
                        var info = new CM_COLUMNINFO { cbSize = (uint)Marshal.SizeOf<CM_COLUMNINFO>(), dwMask = CM_MASK_WIDTH, uWidth = (uint)(width * sc), wszName = "" };
                        var k = key;
                        Call<ColumnInfoFn>(cm, 3)(cm, ref k, ref info);
                    }
                }
                finally { Marshal.Release(cm); }
        }
        finally { Marshal.Release(view); }
    }

    /// <summary>The dialog's active shell view (AddRef'd; release it), or zero.</summary>
    static IntPtr ActiveView(IntPtr dialog)
    {
        IntPtr sb = SendMessage(dialog, WM_GETISHELLBROWSER, IntPtr.Zero, IntPtr.Zero);   // (not AddRef'd)
        if (sb == IntPtr.Zero) return IntPtr.Zero;
        return Call<QueryViewFn>(sb, 15)(sb, out IntPtr view) == 0 ? view : IntPtr.Zero;   // IShellBrowser::QueryActiveShellView
    }

    static IntPtr? Query(IntPtr unk, Guid iid) => Marshal.QueryInterface(unk, in iid, out IntPtr p) == 0 && p != IntPtr.Zero ? p : null;

    /// <summary>A COM object's method by its vtable slot.</summary>
    static T Call<T>(IntPtr obj, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    static readonly Guid IID_IFolderView2 = new("1af3a467-214f-4298-908e-06b03e0b39f9");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetModeSizeFn(IntPtr self, uint mode, int size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetModeSizeFn(IntPtr self, out uint mode, out int size);
    static readonly Guid IID_IFolderView = new("cde725b0-ccc9-4519-917e-325d72fab4ce"), IID_IColumnManager = new("d8ec27bb-3f3b-4042-b10a-4acfd924d453");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryViewFn(IntPtr self, out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetModeFn(IntPtr self, uint mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetModeFn(IntPtr self, out uint mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetColumnsFn(IntPtr self, [In] PROPERTYKEY[] keys, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetColumnsFn(IntPtr self, uint flags, [Out] PROPERTYKEY[] keys, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ColumnCountFn(IntPtr self, uint flags, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ColumnInfoFn(IntPtr self, ref PROPERTYKEY key, ref CM_COLUMNINFO info);

    /// <summary>For --filedialog-test: an open file dialog's view mode and columns (key ids and widths), then it's closed.</summary>
    public static string ProbeAndClose()
    {
        string said = "no file dialog found";
        EnumThreadWindows(GetCurrentThreadId(), (w, _) =>
        {
            if (ClassOf(w) != "#32770" || FindChild(w, "SHELLDLL_DefView") == IntPtr.Zero) return true;
            IntPtr view = ActiveView(w);
            if (view == IntPtr.Zero) { said = "no shell view"; }
            else
                try
                {
                    uint mode = 0;
                    if (Query(view, IID_IFolderView) is { } fv) { Call<GetModeFn>(fv, 3)(fv, out mode); Marshal.Release(fv); }
                    var parts = new List<string>();
                    if (Query(view, IID_IColumnManager) is { } cm)
                    {
                        if (Call<ColumnCountFn>(cm, 5)(cm, CM_ENUM_VISIBLE, out uint n) == 0)
                        {
                            var keys = new PROPERTYKEY[n];
                            Call<GetColumnsFn>(cm, 6)(cm, CM_ENUM_VISIBLE, keys, n);
                            foreach (var k in keys)
                            {
                                var kk = k; var info = new CM_COLUMNINFO { cbSize = (uint)Marshal.SizeOf<CM_COLUMNINFO>(), dwMask = CM_MASK_WIDTH | CM_MASK_NAME, wszName = "" };
                                Call<ColumnInfoFn>(cm, 4)(cm, ref kk, ref info);
                                parts.Add($"{info.wszName} {info.uWidth}px");
                            }
                        }
                        Marshal.Release(cm);
                    }
                    said = $"view mode {mode} ({(mode == FVM_DETAILS ? "Details" : "not Details")}); columns: {string.Join(", ", parts)}";
                }
                finally { Marshal.Release(view); }
            PostMessage(w, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
            return false;
        }, IntPtr.Zero);
        return said;
    }
    /// <summary>For --filedialog-views-test: the open picker's view ("mode:size") and kind; with <paramref name="setTo"/>, it's
    /// then set to that view (as a user's pick); with <paramref name="close"/>, the picker closes.</summary>
    public static string ProbeView(string? setTo, bool close)
    {
        string said = "no file dialog found";
        EnumThreadWindows(GetCurrentThreadId(), (w, _) =>
        {
            if (ClassOf(w) != "#32770" || FindChild(w, "SHELLDLL_DefView") == IntPtr.Zero) return true;
            said = $"{ReadView(w)} {(ListsImages(w) ? "images" : "files")}";
            if (setTo?.Split(':') is [var m, var z] && ActiveView(w) is var view && view != IntPtr.Zero)
                try { if (Query(view, IID_IFolderView2) is { } fv2) { Call<SetModeSizeFn>(fv2, 35)(fv2, uint.Parse(m), int.Parse(z)); Marshal.Release(fv2); } }
                finally { Marshal.Release(view); }
            if (close) PostMessage(w, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return false;
        }, IntPtr.Zero);
        return said;
    }
    const uint CM_ENUM_VISIBLE = 0x2, CM_MASK_NAME = 0x8;
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    static string ClassOf(IntPtr w) { var sb = new System.Text.StringBuilder(64); GetClassName(w, sb, sb.Capacity); return sb.ToString(); }

    static IntPtr FindChild(IntPtr parent, string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, (w, _) => { if (ClassOf(w) == cls) { found = w; return false; } return true; }, IntPtr.Zero);
        return found;
    }

    // ---- Win32 / shell
    const uint WM_GETISHELLBROWSER = 0x0400 + 7;
    const uint FVM_DETAILS = 4;
    const uint CM_MASK_WIDTH = 0x1;
    static readonly Guid PropSummary = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");
    static readonly PROPERTYKEY Name = new(PropSummary, 10), DateModified = new(PropSummary, 14), ItemType = new(PropSummary, 4), Size = new(PropSummary, 12),
        FolderPath = new(new Guid("E3E0584C-B788-4A5A-BB20-7F5A44C9ACDD"), 6);

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY(Guid fmtid, uint pid) { public Guid Fmtid = fmtid; public uint Pid = pid; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CM_COLUMNINFO
    {
        public uint cbSize, dwMask, dwState, uWidth, uDefaultWidth, uIdealWidth;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string wszName;
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint threadId, EnumProc fn, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc fn, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
