namespace MhoExtendedModManager.Model.Gui;

sealed partial class ModelPage
{
    /// <summary>
    /// --model-rigskin-test (scratch library only): a mod with its own model and no saved source opens with that model as the
    /// source (0.60.563), Dress 1 is added and the cloth is skinned onto it (0.60.564). MHO_TEST_RIGSKINSHOT=&lt;png&gt;: the window
    /// after it (the manual's picture).
    /// </summary>
    internal async Task<bool> TestRigSkin(Action<string> say)
    {
        bool ok = true;
        void Check(bool c, string what) { say((c ? "ok   " : "FAIL ") + what); ok &= c; }
        for (int i = 0; i < 1200 && !HasSource; i++) await Task.Delay(100);
        Check(HasSource && sourceFbx?.Contains("(Mod's Model)", StringComparison.Ordinal) == true, $"the mod's own model is the source ({Path.GetFileName(Path.GetDirectoryName(sourceFbx ?? "")) ?? "none"})");
        if (!HasSource) return false;
        for (int i = 0; i < 600 && (refreshing || preview.ShowCount == 0); i++) await Task.Delay(100);
        int sc = preview.ShowCount;
        SetRigs(0, 0, "Dress:1");
        for (int i = 0; i < 1200 && preview.ShowCount == sc; i++) await Task.Delay(100);
        await Task.Delay(2000);
        Check(log.Text.Contains("skinned onto its", StringComparison.Ordinal), "the log says the cloth was skinned onto Dress 1");
        if (Environment.GetEnvironmentVariable("MHO_TEST_RIGSKINSHOT") is { Length: > 0 } shot && FindForm() is { } win)
        {
            modelTabs.Select(0); await Task.Delay(1500);
            using var bmp = new Bitmap(win.Width, win.Height); win.DrawToBitmap(bmp, new Rectangle(0, 0, win.Width, win.Height)); bmp.Save(shot);
            say("shot: " + shot);
        }
        return ok;
    }
}
