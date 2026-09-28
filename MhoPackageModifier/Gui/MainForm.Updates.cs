namespace MhoPackageModifier.Gui;

/// <summary>
/// Updates: a check at start (at most once a day, can be turned off on the Start tab; asked once at first start, so nothing
/// goes over the network without consent) and on request; when a newer release
/// is out, a link in the header. Installing downloads, verifies and swaps the files (Updater), then restarts the app.
/// </summary>
sealed partial class MainForm
{
    readonly LinkLabel updateLink = new() { AutoSize = true, Visible = false, Padding = new Padding(0, 4, 8, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    readonly CheckBox checkUpdates = new() { Text = "Check for Updates When the App Starts", AutoSize = true };
    Updater.Release? available;

    void InitUpdates()
    {
        checkUpdates.Checked = settings.CheckForUpdates;
        checkUpdates.CheckedChanged += (_, _) => settings.CheckForUpdates = checkUpdates.Checked;
        updateLink.LinkClicked += (_, _) => { if (available != null) OfferUpdate(available); };
        if (!settings.UpdateCheckAsked) { Shown += (_, _) => BeginInvoke(AskUpdateConsent); return; }
        if (settings.CheckForUpdates && DateTime.Now - settings.LastUpdateCheck > TimeSpan.FromDays(1)) _ = CheckForUpdates(quiet: true);
    }

    void AskUpdateConsent()
    {
        var answer = MessageBox.Show(this, "Check GitHub for new versions of MHO Package Modifier when the app starts (at most once a day)?\n\n" +
            "The check reads the public release list of github.com/leeper48/MHO-UPK-Tools. Nothing about you or your game is sent, " +
            "and nothing is installed without asking.\n\nYou can change this on the Start tab at any time.",
            "Check for updates?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        settings.UpdateCheckAsked = true;
        checkUpdates.Checked = answer == DialogResult.Yes;                    // sets settings.CheckForUpdates
        SaveSettings();
        if (settings.CheckForUpdates) _ = CheckForUpdates(quiet: true);
    }

    async Task CheckForUpdates(bool quiet)
    {
        if (!quiet) Log("Checking for updates ...");
        var (r, note) = await Updater.LatestAsync();
        settings.LastUpdateCheck = DateTime.Now;
        if (r == null) { if (!quiet) Log($"Update check: {note}."); return; }
        if (r.Version <= Updater.Current) { if (!quiet) Log($"Up to date: v{Updater.Current}."); return; }
        available = r;
        updateLink.Text = $"⬆ Update Available: v{r.Version}";
        updateLink.Visible = true;
        Log($"Update available: v{r.Version} (you have v{Updater.Current}). Click the link at the top right to install it.");
        if (!quiet) OfferUpdate(r);
    }

    void OfferUpdate(Updater.Release r)
    {
        string notes = r.Notes.Length > 1200 ? r.Notes[..1200] + " ..." : r.Notes;
        var answer = MessageBox.Show(this, $"Version {r.Version} is available (you have {Updater.Current}).\n\n{notes}\n\n" +
            "Yes: download, check and install it now, then restart the app.\nNo: open the release page.\nCancel: later.",
            "Update available", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
        if (answer == DialogResult.No) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = r.PageUrl, UseShellExecute = true });
        if (answer != DialogResult.Yes) return;
        if (!tabs.Enabled) { MessageBox.Show(this, "Wait for the current task to finish, then update.", "Update"); return; }
        foreach (var c in busyDisabled) c.Enabled = false;
        UseWaitCursor = true;
        Log($"=== Update to v{r.Version} ===");
        Updater.InstallAsync(r, line => BeginInvoke(() => Log("  " + line))).ContinueWith(t =>
        {
            UseWaitCursor = false;
            foreach (var c in busyDisabled) c.Enabled = true;
            if (t.Result is string err) { Log($"  Not updated: {err}"); MessageBox.Show(this, $"Not updated: {err}", "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            CaptureSettings(); SaveSettings();
            MessageBox.Show(this, $"Updated to v{r.Version}. The app restarts now.", "Update");
            Updater.Restart();
            Close();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
