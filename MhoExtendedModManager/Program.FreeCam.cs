using MhoExtendedModManager.FreeCam;

namespace MhoExtendedModManager;

static partial class Program
{
    /// <summary>FreeCam Options from the command line. Null when <paramref name="cmd"/> isn't one of its commands.</summary>
    static int? FreeCamCommand(string cmd, List<string> rest, Settings settings, string data, ModLibrary lib)
    {
        string? Arg(string flag) { int i = rest.FindIndex(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)); return i > 0 && i + 1 < rest.Count ? rest[i + 1] : null; }
        bool dry = rest.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        string? root = Arg("--game") ?? settings.ResolvedGameRoot(data);
        switch (cmd)
        {
            case "--freecam-status":
            {
                if (root == null) { Console.WriteLine("No game folder set."); return 1; }
                var st = FreeCamBuddy.Detect(root);
                Console.WriteLine($"{st.Kind}{(st.Version.Length > 0 ? " " + st.Version : "")} · loader {(st.Loader ? "present" : "missing")} · {(st.Ours ? "written by this app" : "not written by this app")}");
                Console.WriteLine(st.Detail);
                var fc = FreeCamSettings.Load();
                foreach (var (spec, what) in new[] { (fc.ModeKey, "camera mode key"), (fc.SnapKey, "snap behind key"), (fc.ModePad, "camera mode gamepad button"), (fc.SnapPad, "snap behind gamepad button") })
                    Console.WriteLine($"  {what} {spec}: now {FreeCamBuddy.CurrentCommand(spec) ?? "(nothing)"}");
                return 0;
            }
            case "--freecam-apply":
            case "--freecam-remove":
            {
                if (root == null) { Console.WriteLine("No game folder set."); return 1; }
                var log = new List<string>();
                bool ok = cmd == "--freecam-apply"
                    ? FreeCamBuddy.Apply(root, FreeCamSettings.Load(), NamingMarvelGame(lib), dry, log)
                    : FreeCamBuddy.Remove(root, dry, log);
                foreach (var l in log) Console.WriteLine("  " + l);
                Console.WriteLine(ok ? (dry ? "Dry run: nothing written." : "Done.") : "Stopped.");
                return ok ? 0 : 1;
            }
            case "--freecam-build":
            {
                // --freecam-build <freecam MarvelGame.upk> <out.upk>: build only (settings from data\freecam), for checking / tests
                if (rest.Count < 3) { Console.WriteLine("--freecam-build <freecam MarvelGame.upk> <out.upk>"); return 1; }
                var log = new List<string>();
                byte[] built = FreeCamPatch.BuildPackage(File.ReadAllBytes(rest[1]), FreeCamSettings.Load(), log);
                foreach (var l in log) Console.WriteLine("  " + l);
                File.WriteAllBytes(rest[2], built);
                Console.WriteLine($"Built and checked: {rest[2]} ({built.Length:N0} bytes).");
                return 0;
            }
            case "--freecam-test":
                return FreeCamTest(rest);
        }
        return null;
    }

    static List<(string Name, string Copy)> NamingMarvelGame(ModLibrary lib) =>
        lib.Mods.Where(m => m.Enabled && m.Manifest.UpkReplacements.Contains(FreeCamBuddy.File, StringComparer.OrdinalIgnoreCase)).Select(m => (m.Name, Path.Combine(m.Folder, FreeCamBuddy.File))).ToList();

    /// <summary>
    /// --freecam-test &lt;scratch dir&gt; &lt;freecam MarvelGame.upk&gt; &lt;input config folder&gt;: end to end on copies (needs
    /// MHO_EXTMM_HOME on a scratch folder): a scratch game folder with the Freecam package and a stand-in loader, copies of
    /// the input .ini files; detect, apply, apply with other settings, remove; the package and .ini files must come back
    /// byte for byte.
    /// </summary>
    static int FreeCamTest(List<string> rest)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MHO_EXTMM_HOME"))) { Console.WriteLine("Set MHO_EXTMM_HOME to a scratch folder first."); return 1; }
        if (rest.Count < 4) { Console.WriteLine("--freecam-test <scratch dir> <freecam MarvelGame.upk> <input config folder> [MarvelHUD_SF.upk [reference build]]"); return 1; }
        string dir = Path.GetFullPath(rest[1]);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        string game = Path.Combine(dir, "game"), cooked = Settings.Cooked(game), input = Path.Combine(dir, "input");
        Directory.CreateDirectory(cooked); Directory.CreateDirectory(input);
        Directory.CreateDirectory(Path.GetDirectoryName(FreeCamBuddy.LoaderPath(game))!);
        File.WriteAllText(FreeCamBuddy.LoaderPath(game), "stand-in loader");
        byte[] freecam = File.ReadAllBytes(rest[2]);
        File.WriteAllBytes(FreeCamBuddy.Live(game), freecam);
        // optional: the game's MarvelHUD_SF.upk (overhead labels) and a reference build of it with names + titles hidden
        byte[]? hud = rest.Count > 4 ? File.ReadAllBytes(rest[4]) : null;
        if (hud != null) File.WriteAllBytes(HudLabels.Live(game), hud);
        foreach (var f in new[] { "MarvelInput.ini", "MarvelInputUserSettings.ini" }) File.Copy(Path.Combine(rest[3], f), Path.Combine(input, f));
        Environment.SetEnvironmentVariable("MHO_FREECAM_INPUT_DIR", input);
        if (Directory.Exists(FreeCamSettings.Folder)) Directory.Delete(FreeCamSettings.Folder, true);
        // (the copies may already carry FreeCam Options / MHO Camera lines: start from files without them)
        FreeCamBuddy.Remove(game, dryRun: false, [], checkGameRunning: false);
        var iniBefore = new[] { "MarvelInput.ini", "MarvelInputUserSettings.ini" }.Select(f => File.ReadAllBytes(Path.Combine(input, f))).ToList();

        int pass = 0, fail = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}"); if (ok) pass++; else fail++; }
        var log = new List<string>();
        void Dump() { foreach (var l in log) Console.WriteLine("        " + l); log.Clear(); }

        var st = FreeCamBuddy.Detect(game);
        Check(st.Kind == FreeCamBuddy.Kind.Freecam && st.CanApply, $"detects Freecam ({st.Kind} {st.Version})");
        // one button for both actions (RT+LB), told apart by press: short = mode, double = snap
        var cfg = new FreeCamSettings { ModePad = "RT+XboxTypeS_LeftShoulder", SnapPad = "RT+XboxTypeS_LeftShoulder", ModePress = "Short", SnapPress = "Double", InvertByDefault = true };
        Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: true, log, checkGameRunning: false) && File.ReadAllBytes(FreeCamBuddy.Live(game)).AsSpan().SequenceEqual(freecam), "dry run writes nothing"); Dump();
        Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false), "apply"); Dump();
        st = FreeCamBuddy.Detect(game);
        Check(st.Kind == FreeCamBuddy.Kind.Buddy && st.Ours, $"detects FreeCam Options, written here ({st.Kind}, ours {st.Ours})");
        string ini = File.ReadAllText(Path.Combine(input, "MarvelInput.ini"));
        Check(ini.Contains("Name=\"F8\",Command=\"CameraZoomToggle\"") && ini.Contains("Name=\"F4\",Command=\"TraceAllMovieClips\"")
              && ini.Contains("Name=\"XboxTypeS_LeftShoulder\",Command=\"ToggleTraceActorLock | OnRelease ToggleTraceActorLock\",Control=False,Shift=True,Alt=False,bIgnoreCtrl=True,bIgnoreShift=False")
              && System.Text.RegularExpressions.Regex.Matches(ini, "Command=\"ToggleTraceActorLock").Count == 1 && !ini.Contains("MarvelGFXHidePvPScores"),
              "bindings written (F8, F4, one shared RT+LB line on press and release)");
        Check(FreeCamBuddy.CurrentCommand("RT+XboxTypeS_LeftShoulder") is var cur && cur?.StartsWith("ToggleTraceActorLock") != true, $"the button's own command is still read without ours ({cur ?? "(nothing)"})");
        // two buttons: mode on D-Pad Up (long), snap on RT+LB (short): each its own tracker and command
        cfg.ModePad = "XboxTypeS_DPad_Up"; cfg.ModePress = "Long"; cfg.SnapPress = "Short";
        Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false), "apply with two buttons"); Dump();
        string ini2 = File.ReadAllText(Path.Combine(input, "MarvelInput.ini"));
        Check(ini2.Contains("Name=\"XboxTypeS_DPad_Up\",Command=\"ToggleTraceActorLock | OnRelease ToggleTraceActorLock\"")
              && ini2.Contains("Name=\"XboxTypeS_LeftShoulder\",Command=\"MarvelGFXHidePvPScores | OnRelease MarvelGFXHidePvPScores\",Control=False,Shift=True"),
              "two buttons: D-Pad Up → tracker 1, RT+LB → tracker 2");
        // three buttons: selfie on D-Pad Down (short) → tracker 3 (state and timer packed in one field); F3 = selfie key
        cfg.SelfiePad = "XboxTypeS_DPad_Down"; cfg.SelfiePress = "Short";
        Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false), "apply with three buttons (Selfie added)"); Dump();
        string ini2b = File.ReadAllText(Path.Combine(input, "MarvelInput.ini"));
        Check(ini2b.Contains("Name=\"XboxTypeS_DPad_Down\",Command=\"MarvelGFxTogglePvPScorePanel | OnRelease MarvelGFxTogglePvPScorePanel\"")
              && ini2b.Contains("Name=\"F3\",Command=\"MarvelGFXShowPvPScores\""), "three buttons: D-Pad Down → tracker 3; F3 → Selfie");
        cfg.SelfiePad = "";
        byte[] first = File.ReadAllBytes(FreeCamBuddy.Live(game));
        cfg.ZoomStepsCloser = 4; cfg.TiltDown = 60; cfg.ModePad = ""; cfg.SnapPad = "";
        cfg.BehindKeepZoom = false; cfg.DefaultGameZoom = false; cfg.BehindLockTilt = true; cfg.WallsFade = true;   // both set zooms, Lock Tilt and walls fade built (0.60.451)
        Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false), "apply again with other settings (rebuilt from the kept Freecam package)"); Dump();
        byte[] second = File.ReadAllBytes(FreeCamBuddy.Live(game));
        Check(!second.AsSpan().SequenceEqual(first) && FreeCamBuddy.Detect(game).Ours, "the package changed and is still recognized as ours");
        string ini3 = File.ReadAllText(Path.Combine(input, "MarvelInput.ini"));
        Check(!ini3.Contains("Command=\"ToggleTraceActorLock") && !ini3.Contains("Command=\"MarvelGFXHidePvPScores"), "gamepad buttons set to None are taken out");
        Check(FreeCamBuddy.KeepsOver(game, FreeCamBuddy.File, rest[2]), "the Mods tab's Apply leaves it over a mod's Freecam copy (KeepsOver)");
        Check(FreeCamBuddy.Apply(game, cfg, [("Freecam Mod", rest[2])], dryRun: true, log), "allowed over a mod whose MarvelGame.upk is Freecam's"); Dump();
        Check(!FreeCamBuddy.Apply(game, cfg, [("Other Mod", FreeCamBuddy.LoaderPath(game))], dryRun: false, log, checkGameRunning: false), "refused when a mod's MarvelGame.upk isn't Freecam's"); Dump();
        string snapIni = File.ReadAllText(Path.Combine(input, "MarvelInput.ini"));
        Check(snapIni.Contains("Name=\"F4\",Command=\"TraceAllMovieClips\""), "Snap Behind bound (F4)");
        if (hud != null)
        {
            byte[] Hud() => File.ReadAllBytes(HudLabels.Live(game));
            cfg.HideNames = true; cfg.HideTitles = true;
            Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: true, log, checkGameRunning: false) && Hud().AsSpan().SequenceEqual(hud), "labels: dry run writes nothing"); Dump();
            Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false), "labels: apply with names and titles hidden"); Dump();
            byte[] namesTitles = Hud();
            Check(!namesTitles.AsSpan().SequenceEqual(hud) && HudLabels.IsOurs(game), "labels: MarvelHUD_SF.upk changed and recognized as ours");
            if (rest.Count > 5) Check(namesTitles.AsSpan().SequenceEqual(File.ReadAllBytes(rest[5])), "labels: identical to the build tested in game");
            Check(HudLabels.Owns(game, HudLabels.File) && HudLabels.KeepsOver(game, HudLabels.File, rest[4]), "labels: the Mods tab's Apply leaves it (left-over and over a mod's copy)");
            cfg.HideNames = false; cfg.HideTitles = false; cfg.HideGuilds = true;
            Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false), "labels: apply with only guild names hidden (rebuilt from the kept copy)"); Dump();
            byte[] guilds = Hud();
            Check(!guilds.AsSpan().SequenceEqual(namesTitles) && !guilds.AsSpan().SequenceEqual(hud) && guilds.Length == namesTitles.Length, "labels: a different build of the same size");
            cfg.HideGuilds = false; cfg.HideLevels = true;
            Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false), "labels: apply with only level numbers hidden"); Dump();
            byte[] levels = Hud();
            int diffs = 0; for (int i = 0; i < Math.Min(levels.Length, hud.Length); i++) if (levels[i] != hud[i]) diffs++;
            Check(!levels.AsSpan().SequenceEqual(guilds) && levels.Length == guilds.Length, $"labels: level numbers a build of their own, same size ({diffs} bytes differ from the game's file)");
            cfg.HideLevels = false;
            Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false) && Hud().AsSpan().SequenceEqual(hud) && !HudLabels.IsOurs(game), "labels: none hidden puts the game's file back byte for byte"); Dump();
            cfg.HideNames = true;
            Check(FreeCamBuddy.Apply(game, cfg, [], dryRun: false, log, checkGameRunning: false) && HudLabels.IsOurs(game), "labels: names hidden again"); Dump();
            cfg.HideNames = false;
        }
        string gameIni = Path.Combine(input, "MarvelGame.ini");
        File.WriteAllText(gameIni, "[Engine.HUD]\r\nConsoleMessageCount=4\r\nConsoleFontSize=1105500700\r\nMessageFontOffset=911879322\r\nbShowHUD=true\r\n");
        Check(FreeCamBuddy.Remove(game, dryRun: false, log, checkGameRunning: false), "remove"); Dump();
        Check(File.ReadAllText(gameIni) == "[Engine.HUD]\r\nConsoleMessageCount=4\r\nConsoleFontSize=5\r\nMessageFontOffset=0\r\nbShowHUD=true\r\n",
            "Remove clears Remember Camera's saved camera in MarvelGame.ini (the game crashed on it without FreeCam Options), rest kept");
        if (hud != null) Check(File.ReadAllBytes(HudLabels.Live(game)).AsSpan().SequenceEqual(hud), "labels: Remove puts MarvelHUD_SF.upk back byte for byte");
        Check(File.ReadAllBytes(FreeCamBuddy.Live(game)).AsSpan().SequenceEqual(freecam), "MarvelGame.upk is Freecam's again, byte for byte");
        Check(new[] { "MarvelInput.ini", "MarvelInputUserSettings.ini" }.Select((f, i) => File.ReadAllBytes(Path.Combine(input, f)).AsSpan().SequenceEqual(iniBefore[i])).All(x => x), "both input files are as before, byte for byte");
        Check(FreeCamBuddy.Detect(game).Kind == FreeCamBuddy.Kind.Freecam, "detects Freecam again");
        File.Delete(FreeCamBuddy.LoaderPath(game));
        Check(!FreeCamBuddy.Detect(game).CanApply && !FreeCamBuddy.Apply(game, cfg, [], dryRun: true, log, checkGameRunning: false), "refused without Freecam's loader"); Dump();
        Console.WriteLine($"{pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }
}
