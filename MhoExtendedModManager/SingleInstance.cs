using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// One window per copy of the app (Kurt, 2026-10-01: clicking the icon again should bring the open window to the front, not
/// open another). A lock named after the app's folder: a second start from the same folder brings the first one's window
/// forward (restored if minimized) and exits. A copy in another folder has its own data folder, so it may run alongside.
/// The updater's restart (MHO_EXTMM_RESTART=1) waits for the closing copy to let go of the lock instead.
/// </summary>
static class SingleInstance
{
    static Mutex? held;

    /// <summary>True: this is the only window of this copy (go on); false: the other one was brought forward (exit).</summary>
    public static bool Claim()
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.TrimEnd('\\', '/').ToLowerInvariant())))[..16];
        held = new Mutex(true, @"Local\MHO_Ext_ModManager_" + id, out bool first);
        if (first) return true;
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_RESTART") == "1")
        {
            try { if (held.WaitOne(30000)) return true; }
            catch (AbandonedMutexException) { return true; }   // the old copy ended without letting go: ours now
        }
        BringOtherForward();
        return false;
    }

    static void BringOtherForward()
    {
        var me = Process.GetCurrentProcess();
        string myExe = Environment.ProcessPath ?? "";
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            try
            {
                if (p.Id == me.Id || !string.Equals(p.MainModule?.FileName, myExe, StringComparison.OrdinalIgnoreCase)) continue;
                IntPtr h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);   // allowed: this process was just started by the user, so it holds the foreground
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    const int SW_RESTORE = 9;
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
}
