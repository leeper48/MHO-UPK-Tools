using System.Diagnostics;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The main window's Nexus strip: update checks, linking, updates, sign-in, watching Downloads.</summary>
sealed partial class MainForm
{
    // ---- Nexus

    /// <summary>Fetches every linked mod's Nexus info; the list then shows ↑ on mods with a newer version.</summary>
    async void CheckNexus(bool manual)
    {
        if (lib == null) return;
        int linked = lib.Mods.Count(m => m.NexusModId != null);
        if (linked == 0) { if (manual) Dialog.Show(this, "No mod is linked to a Nexus page yet. Mods installed from a Nexus download are linked by their file name; for others, right-click → Nexus → Link to Nexus Page.", "Check Mods on Nexus"); return; }
        var l = lib;
        nexusBusy = $"Checking {linked} Linked Mod(s) on Nexus…"; UpdateNexusStatus();
        int n; List<string> problems;
        try { (n, problems) = await Task.Run(() => NexusUpdates.Check(l, nexus)); }
        finally { nexusBusy = null; UpdateNexusStatus(); }
        try { nexus.Save(Settings.Home); } catch (IOException) { }
        int updates = l.Mods.Count(m => NexusUpdates.UpdateFor(m, nexus) != null);
        note = updates > 0 ? $"{updates} Mod Update(s) on Nexus" : $"Checked {n} Mod(s) on Nexus: Up to Date";
        Reload();
        if (manual) Dialog.Show(this, (updates > 0 ? $"{updates} mod(s) have a newer version on Nexus (marked ↑ in the list; filter is:update)." : $"All {n} linked mod(s) are up to date.") +
            (problems.Count > 0 ? "\n\nNot checked: " + string.Join("; ", problems) : ""), "Check Mods on Nexus", MessageBoxButtons.OK, problems.Count > 0 && n == 0 ? MessageBoxIcon.Error : MessageBoxIcon.None);
    }

    /// <summary>The Nexus strip's state: connected?, anything linked?, checked when?, updates?</summary>
    void UpdateNexusStatus()
    {
        if (lib == null) { nexusStatus.Set(NexusStatus.State.NotConnected, "Nexus", "No mod library yet"); return; }
        int linked = lib.Mods.Count(m => m.NexusModId != null);
        int updates = lib.Mods.Count(m => NexusUpdates.UpdateFor(m, nexus) != null);
        string linkedText = $"{linked} of {lib.Mods.Count} Linked" + (NexusAuth.Available && NexusAuth.Load(Settings.Home) is { } signed ? $"  ·  {signed.UserName}{(signed.Premium ? " (Premium)" : "")}" : "");
        NexusStatus.State s; string one, two, tip;
        if (nexusBusy != null) { s = NexusStatus.State.Busy; one = nexusBusy; two = linkedText; tip = "Working with Nexus…"; }
        else if (linked == 0)
        {
            s = NexusStatus.State.NothingLinked; one = "Nexus: No Mods Linked Yet"; two = "Click to Find Your Mods on Nexus";
            tip = "Only linked mods can be checked for updates. Click to look up your mods on Nexus and link the right ones.";
        }
        else if (nexus.Checked is not DateTime at)
        {
            s = NexusStatus.State.NotChecked; one = "Nexus: Not Checked Yet"; two = linkedText + "  ·  Click to Check";
            tip = "Click to ask Nexus whether your linked mods have newer versions.";
        }
        else if (updates > 0)
        {
            s = NexusStatus.State.Updates; one = $"{updates} Update(s) on Nexus  ·  {(IsUpdateFilter ? "Show All" : "Show")}"; two = $"Checked {NexusStatus.Ago(at)}  ·  {linkedText}";
            tip = IsUpdateFilter ? "Click to show every mod again." : "Click to show only the mods with an update (filter is:update). Click a mod's green Update mark to update it.";
        }
        else if (DateTime.Now - at > TimeSpan.FromDays(1))
        {
            s = NexusStatus.State.Stale; one = $"Nexus: Last Checked {NexusStatus.Ago(at)}"; two = linkedText + "  ·  Click to Check";
            tip = "The last check is over a day old. Click to check again.";
        }
        else
        {
            s = NexusStatus.State.UpToDate; one = "Nexus: Up to Date"; two = $"Checked {NexusStatus.Ago(at)}  ·  {linkedText}";
            tip = "Every linked mod had its newest version at the last check. Click to check again.";
        }
        nexusStatus.Set(s, one, two);
        if (tips.GetToolTip(nexusStatus) != tip) tips.SetToolTip(nexusStatus, tip);
    }

    bool IsUpdateFilter => filter.Text.Trim().Equals("is:update", StringComparison.OrdinalIgnoreCase);

    /// <summary>A click on the Nexus status: its next step (connect, find, check, show the updates).</summary>
    void NexusStatusClicked()
    {
        switch (nexusStatus.Current)
        {
            case NexusStatus.State.Busy: return;
            case NexusStatus.State.NothingLinked: FindOnNexus(null); return;
            case NexusStatus.State.Updates: filter.Text = IsUpdateFilter ? "" : "is:update"; return;
            default: CheckNexus(manual: true); return;
        }
    }

    Button[] nexusFolded = [];
    readonly Dictionary<Button, Action> nexusActions = [];

    /// <summary>Shows as many of the Nexus strip's buttons as fit beside a status at least 230 px (scaled) wide; the rest are in ▾.</summary>
    void FitNexusRow(Control row, Control buttons)
    {
        float s = DeviceDpi / 96f;
        int room = row.ClientSize.Width - (int)(230 * s);
        static int W(Control c) => (c is Button { AutoSize: false } ? c.Width : c.GetPreferredSize(Size.Empty).Width) + c.Margin.Horizontal;   // (icon buttons: their own size)
        int always = buttons.Controls.Cast<Control>().Where(c => !nexusFolded.Contains(c)).Sum(W) + buttons.Margin.Horizontal;
        int used = always;
        // Keep from the most useful (Check, Find, Browse); fold from the other end.
        foreach (var b in nexusFolded.Reverse())
        {
            int w = W(b);
            bool fits = used + w <= room;
            if (fits) used += w;
            if (b.Visible != fits) b.Visible = fits;
        }
    }

    ContextMenuStrip NexusBarMenu()
    {
        var menu = NewMenu();
        // Buttons folded away on a narrow list are here instead.
        var folded = nexusFolded.Where(b => !b.Visible).ToList();
        foreach (var b in folded.AsEnumerable().Reverse()) { var act = nexusActions[b]; menu.Items.Add(string.IsNullOrEmpty(b.Text) ? b.AccessibleName : b.Text, null, (_, _) => act()); }
        if (folded.Count > 0) menu.Items.Add(new ToolStripSeparator());
        if (NexusAuth.Available)
        {
            if (NexusAuth.Load(Settings.Home) is { } who)
            {
                menu.Items.Add(new ToolStripMenuItem($"Signed In as {who.UserName} ({(who.Premium ? "Premium" : "Free Account")})") { Enabled = false });
                menu.Items.Add("Sign Out of Nexus", null, (_, _) => SignOutNexus());
            }
            else menu.Items.Add("Sign In with Nexus (One-Click Updates for Premium)", null, (_, _) => SignInNexus());
            menu.Items.Add(new ToolStripSeparator());
        }
        var atStart = new ToolStripMenuItem("Check for Updates at Start") { Checked = settings.NexusCheckAtStart };
        atStart.Click += (_, _) => { settings.NexusCheckAtStart = !settings.NexusCheckAtStart; settings.Save(); };
        menu.Items.Add(atStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Mods With Updates", null, (_, _) => filter.Text = "is:update");
        menu.Items.Add("Show Linked Mods", null, (_, _) => filter.Text = "is:nexus");
        menu.Items.Add("Sort: Updates First", null, (_, _) => { settings.ListSort = "update"; settings.Save(); FillList(Selected?.FolderName); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Marvel Heroes Omega Mods on Nexus", null, (_, _) => Process.Start(new ProcessStartInfo(Nexus.SiteMods) { UseShellExecute = true }));
        return menu;
    }

    /// <summary>"Overrides A, B · overridden by C" for a conflicting mod (list tooltips).</summary>
    string? ConflictSummary(Mod m)
    {
        if (!conflictWith.TryGetValue(m, out var d)) return null;
        var wins = d.Keys.Where(o => o.Priority > m.Priority).OrderBy(o => o.Priority).Select(o => $"\"{o.Name}\"").ToList();
        var loses = d.Keys.Where(o => o.Priority < m.Priority).OrderBy(o => o.Priority).Select(o => $"\"{o.Name}\"").ToList();
        return string.Join("\n", new[] { loses.Count > 0 ? "Overridden in part by " + string.Join(", ", loses) + " (higher in the list)" : null,
                                         wins.Count > 0 ? "Overrides " + string.Join(", ", wins) + " where they change the same thing" : null }.Where(x => x != null));
    }

    ToolStripMenuItem NexusMenu(Mod m)
    {
        var sub = new ToolStripMenuItem("Nexus");
        if (m.NexusModId is int id)
        {
            sub.DropDownItems.Add("Open the Nexus Page", null, (_, _) => Process.Start(new ProcessStartInfo(Nexus.SiteMods + id) { UseShellExecute = true }));
            if (NexusUpdates.UpdateFor(m, nexus) is string v)
            {
                sub.DropDownItems.Add($"Update to v{v.TrimStart('v', 'V')}", null, (_, _) => UpdateFromNexus(m));
                if (NexusAuth.Available && NexusAuth.Load(Settings.Home)?.Premium == true)
                    sub.DropDownItems.Add("Download From the Files Page Instead", null, (_, _) => UpdateFromNexus(m, manual: true));
                if (NexusUpdates.LatestFor(m, nexus) is { } lf) sub.DropDownItems.Add("Ignore This Update", null, (_, _) => IgnoreUpdate(m, lf.FileId));
            }
            if (nexus.Mods.TryGetValue(id, out var pi) && Nexus.Lines(pi).Count > 1)
                sub.DropDownItems.Add("Choose the Nexus File", null, (_, _) => ChooseNexusFile(m));
            sub.DropDownItems.Add("Check for an Update Now", null, (_, _) => CheckNexus(manual: true));
            sub.DropDownItems.Add("Change the Nexus Link", null, (_, _) => LinkToNexus(m));
            if (m.NexusLink != null) sub.DropDownItems.Add("Unlink", null, (_, _) => { if (lib != null) { m.NexusLink = null; lib.SaveState(); Reload(); } });
        }
        else
        {
            sub.DropDownItems.Add("Find on Nexus", null, (_, _) => FindOnNexus(m));
            sub.DropDownItems.Add("Link to Nexus Page", null, (_, _) => LinkToNexus(m));
        }
        sub.Enabled = !readOnly;
        return sub;
    }

    /// <summary>
    /// Find My Mods on Nexus: the game's mod list from Nexus (public, no key), matched against the unlinked mods (or one
    /// mod); the user ticks the right pages and they're linked.
    /// </summary>
    async void FindOnNexus(Mod? only)
    {
        if (readOnly || lib == null) return;
        var targets = only != null ? [only] : lib.Mods.Where(m => m.NexusModId == null).ToList();
        if (targets.Count == 0) { Dialog.Show(this, "Every mod is linked to a Nexus page already.", "Find My Mods on Nexus"); return; }
        List<NexusMatch.NexusMod> all;
        try
        {
            UseWaitCursor = true;
            nexusBusy = "Reading the Nexus Mod List…"; UpdateNexusStatus();
            var progress = new Progress<string>(s => status.Text = s);
            all = await NexusMatch.AllMods(progress);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        { Dialog.Show(this, "Couldn't read the mod list from Nexus: " + ex.Message, "Find My Mods on Nexus", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { UseWaitCursor = false; nexusBusy = null; UpdateNexusStatus(); }
        using var f = new NexusScanForm(targets, all);
        if (f.ShowDialog(this) != DialogResult.OK || f.Confirmed.Count == 0) { Reload(); return; }
        var l = lib;
        Change($"link {f.Confirmed.Count} mod(s) to Nexus", () =>
        {
            foreach (var (m, id, version) in f.Confirmed)
                if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod mm)
                    mm.NexusLink = new NexusLink { ModId = id, Version = mm.Manifest.Version, Installed = DateTime.Now };
            return true;
        });
        CheckNexus(manual: false);
    }

    void LinkToNexus(Mod m)
    {
        if (lib == null) return;
        string? text = Ui.Prompt(this, "Link to Nexus Page", $"The Nexus page of \"{m.Name}\" (paste its address, or the mod number):", m.NexusModId is int id ? Nexus.SiteMods + id : "");
        if (text == null) return;
        if (Nexus.ParseModId(text) is not int newId) { Dialog.Show(this, "That isn't a Nexus mod page (…/marvelheroesomega/mods/<number>) or a mod number.", "Not a Nexus Page", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        m.NexusLink = new NexusLink { ModId = newId, Version = m.Manifest.Version, Installed = DateTime.Now };
        lib.SaveState();
        CheckNexus(manual: false);
        Reload();
    }

    /// <summary>
    /// Updates a mod from Nexus. Signed in with Premium: downloads the newest main file and installs it in place (place,
    /// on/off, lock, tags and note kept). Otherwise (or <paramref name="manual"/>): opens its Files page, and when the
    /// downloaded file arrives in Downloads, installs it the same way. (Nexus gives apps download links only for signed-in
    /// Premium members; no personal API keys.)
    /// </summary>
    async void UpdateFromNexus(Mod m, bool manual = false)
    {
        if (readOnly || lib == null || m.NexusModId is not int id) return;
        // A page with several files (a variant): which one this mod is must be known first, or the wrong one goes in.
        if (NexusUpdates.NeedsChoice(m, nexus) && !ChooseNexusFile(m, "Before updating: this page has several files.")) return;
        if (lib.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod fresh) m = fresh;
        if (!manual && NexusAuth.Available && NexusAuth.Load(Settings.Home)?.Premium == true)
        {
            try
            {
                UseWaitCursor = true;
                string? token = await NexusAuth.AccessToken(Settings.Home);
                if (token == null) { UseWaitCursor = false; Dialog.Show(this, "Your Nexus sign-in has ended (it may have been revoked). Sign in again from the ▾ menu in the Nexus bar, or download the update from the Files page.", "Not Updated", MessageBoxButtons.OK, MessageBoxIcon.Warning); UpdateNexusStatus(); return; }
                var progress = new Progress<string>(s => status.Text = s);
                var (path, file) = await NexusUpdates.DownloadLatest(m, token, nexus, Settings.Home, progress);
                UseWaitCursor = false;
                await FinishNexusUpdate(m, id, path, file.FileId, file.Version);
            }
            catch (Exception ex) when (ex is Nexus.NexusException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
            {
                UseWaitCursor = false;
                if (ex is NexusAuth.SignedOutException) UpdateNexusStatus();
                if (Dialog.Show(this, ex.Message + "\n\nDownload it from the mod's Files page instead?", "Not Updated", MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    UpdateFromNexus(m, manual: true);
            }
            return;
        }
        WatchDownloads(m, id);
        Process.Start(new ProcessStartInfo(Nexus.SiteMods + id + "?tab=files") { UseShellExecute = true });
        string? which = NexusUpdates.LineFor(m, nexus);
        status.Text = Ui.TitleCase($"Download the update of \"{m.Name}\" on Nexus" + (which != null ? $" (the file \"{which}\"" + ", Manual Download)" : " (Manual Download)") +
            ": the app installs it when it arrives in your Downloads folder");
    }

    /// <summary>
    /// Which file on the mod's Nexus page it is (a page can have a default and a variant side by side). Saved as
    /// NexusLink.File, one undo step; the update check then follows only that file. False when cancelled.
    /// </summary>
    bool ChooseNexusFile(Mod m, string? why = null)
    {
        if (readOnly || lib == null || m.NexusModId is not int id || !nexus.Mods.TryGetValue(id, out var info)) return false;
        using var f = new NexusFileForm(m, info, NexusUpdates.LineFor(m, nexus), why);
        if (f.ShowDialog(this) != DialogResult.OK || f.Chosen is not { } file) return false;
        var l = lib;
        Change($"follow the Nexus file \"{file.Name}\" for \"{m.Name}\"", () =>
        {
            if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is not Mod mm) return false;
            var link = mm.NexusLink ?? new NexusLink { ModId = id, Version = mm.Manifest.Version, Installed = DateTime.Now };
            link.File = file.Name;
            link.Ignore = null;
            mm.NexusLink = link;
            return true;
        });
        return true;
    }

    /// <summary>Sign in with Nexus (OAuth in the browser) for one-click updates.</summary>
    async void SignInNexus()
    {
        if (Dialog.Show(this, "Your browser opens Nexus Mods' sign-in page. Sign in there and allow MHO Extended Mod Manager; then come back here.\n\n" +
                "The app only uses the sign-in to download the updates you choose (Nexus gives apps download links for Premium members only). " +
                "Your password is never seen by the app, and the sign-in stays on this PC, encrypted for your Windows user. Sign out any time from this menu.",
                "Sign In with Nexus", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        nexusBusy = "Waiting for the Nexus Sign-In in Your Browser…"; UpdateNexusStatus();
        try
        {
            var login = await NexusAuth.SignIn(Settings.Home, url => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return Task.CompletedTask; });
            nexusBusy = null; UpdateNexusStatus();
            Activate();
            Dialog.Show(this, login.Premium ? $"Signed in as {login.UserName} (Premium): Update now downloads and installs with one click." :
                $"Signed in as {login.UserName}. Nexus gives apps download links only for Premium members, so updates still open the mod's Files page, and the app installs the file from your Downloads folder.",
                "Signed In", MessageBoxButtons.OK, MessageBoxIcon.None);
        }
        catch (Exception ex) when (ex is Nexus.NexusException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or System.Security.Cryptography.CryptographicException)
        { nexusBusy = null; UpdateNexusStatus(); Activate(); Dialog.Show(this, ex.Message, "Not Signed In", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async void SignOutNexus()
    {
        await NexusAuth.SignOut(Settings.Home);
        UpdateNexusStatus();
        status.Text = Ui.TitleCase("Signed out of Nexus (the app's access was also revoked at Nexus)");
    }

    async Task FinishNexusUpdate(Mod m, int id, string archive, long? fileId, string? version)
    {
        string folder = m.FolderName;
        if (fileId == null && Nexus.FromFileName(archive) is { } fn && nexus.Mods.TryGetValue(id, out var ci)) fileId = Nexus.FileOf(ci, fn)?.FileId;
        var done = await InstallAsync([archive], m, ask: false, showLog: false);
        if (done.Count == 0 || lib == null)
        {
            // Say why (the install log), and offer to ignore that file: e.g. a Nexus file for another tool (no manifest.json).
            bool noManifest = !ArchiveHasManifest(archive);
            string why = noManifest
                ? "It isn't a mod for this manager or MHModManager: it has no manifest.json. It's probably for another tool; the mod's Nexus page says how to install it."
                : string.Join("\n", lastInstallLog.Skip(1).Take(8)).Trim();
            long? ignore = fileId ?? NexusUpdates.LatestFor(m, nexus)?.FileId;
            if (Dialog.Show(this, $"{Path.GetFileName(archive)} couldn't be installed as the update of \"{m.Name}\".\n\n{why}" +
                    (ignore != null ? "\n\nIgnore this Nexus file from now on? The Update mark goes away until a newer file is uploaded." : ""),
                    "Not Updated", ignore != null ? MessageBoxButtons.YesNo : MessageBoxButtons.OK, MessageBoxIcon.Error) == DialogResult.Yes && ignore != null)
                IgnoreUpdate(m, ignore.Value);
            return;
        }
        string? fileName = fileId is long fi && nexus.Mods.TryGetValue(id, out var ri) ? ri.Files.FirstOrDefault(x => x.FileId == fi)?.Name : null;
        NexusUpdates.Record(lib, folder, id, fileId, version ?? Nexus.FromFileName(archive)?.Version, fileName);
        Reload();
        SelectMod(folder);
        Dialog.ShowLog(this, "Updated", $"\"{m.Name}\" is now v{(version ?? "?").TrimStart('v', 'V')}, from Nexus.\n\nIt kept its place, on/off, lock, tags and note." +
            (m.Enabled ? "\n\nIt's on: Apply Changes (Ctrl+Enter) puts the new version in the game." : ""), Dialog.Tone.Good);
    }

    static bool ArchiveHasManifest(string archive)
    {
        try
        {
            using var a = SharpCompress.Archives.ArchiveFactory.Open(archive);
            return a.Entries.Any(e => !e.IsDirectory && (e.Key ?? "").Replace('\\', '/').Split('/').Last().Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException) { return true; }
    }

    /// <summary>Not an update for this user: this Nexus file no longer marks the mod (a newer upload does again).</summary>
    void IgnoreUpdate(Mod m, long fileId)
    {
        if (lib == null) return;
        var l = lib;
        Change($"ignore the Nexus update of \"{m.Name}\"", () =>
        {
            if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod mm && mm.NexusLink is NexusLink link) link.Ignore = fileId;
            else if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod m2 && m2.NexusModId is int mid) m2.NexusLink = new NexusLink { ModId = mid, Installed = DateTime.Now, Ignore = fileId };
            return true;
        });
    }

    /// <summary>Free accounts: watch Downloads (30 minutes) for this mod's file from Nexus ("Name-&lt;id&gt;-&lt;version&gt;-&lt;time&gt;.zip").</summary>
    void WatchDownloads(Mod m, int id)
    {
        downloadWatch?.Dispose();
        string? folder = DownloadsFolder();
        if (folder == null || !Directory.Exists(folder)) return;
        var w = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        downloadWatch = w;
        var until = DateTime.Now.AddMinutes(30);
        bool taken = false;
        async void Seen(string path)
        {
            if (taken || DateTime.Now > until || Nexus.FromFileName(path) is not { } f || f.ModId != id) return;
            // Wait until the browser has finished writing it (size steady, no .part / .crdownload).
            long last = -1;
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(1000);
                if (!File.Exists(path)) return;
                long len = new FileInfo(path).Length;
                if (len > 0 && len == last) break;
                last = len;
            }
            if (taken) return;
            taken = true;
            BeginInvoke(async () =>
            {
                downloadWatch?.Dispose(); downloadWatch = null;
                if (Dialog.Show(this, $"{Path.GetFileName(path)} arrived in Downloads.\n\nInstall it as the update of \"{m.Name}\" (v{f.Version})?", "Nexus Update Downloaded", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    await FinishNexusUpdate(m, id, path, null, f.Version);
            });
        }
        w.Created += (_, e) => Seen(e.FullPath);
        w.Renamed += (_, e) => Seen(e.FullPath);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string? DownloadsFolder()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) != 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(p); }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); }
    }
}
