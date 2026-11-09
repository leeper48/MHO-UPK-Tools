using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager.FreeCam;

/// <summary>
/// FreeCam Options: options on top of Marvel Heroes FreeCam (M.H.F.C.) by Michael (MichaelMerc on Nexus). It needs Freecam's
/// Full install (its MarvelGame.upk camera code and its winmm.dll loader); it never adds or removes files in the game folder.
/// Apply keeps a verified copy of the Freecam package it started from (data\freecam\base), builds from it with the chosen
/// settings and writes MarvelGame.upk through MeshImport.WriteLive (temp file, verify, swap, undo history; no .bak beside it).
/// Remove writes that Freecam package back, so Freecam's own installer still recognizes its file. Key bindings go into the
/// user's input .ini files (Documents\My Games\Marvel Heroes\MarvelGame\Config), not the game folder.
/// The Mods tab's Apply never touches MarvelGame.upk unless a mod names it (Applier: left-over exclusion).
/// </summary>
static class FreeCamBuddy
{
    public const string File = "MarvelGame.upk";
    public const string NexusUrl = "https://www.nexusmods.com/marvelheroesomega/mods/378";
    public const string Credit = "Marvel Heroes FreeCam (M.H.F.C.) by Michael (MichaelMerc on Nexus)";

    public enum Kind { Missing, Stock, Freecam, Buddy, Other }

    public sealed record Status(Kind Kind, string Version, bool Loader, bool Ours, string Detail)
    {
        public bool CanApply => Loader && (Kind == Kind.Freecam || Kind == Kind.Buddy && Ours);
    }

    sealed class State
    {
        public string? BaseSha { get; set; }
        public string? WrittenSha { get; set; }
        public string? GameRoot { get; set; }
    }

    static string StatePath => Path.Combine(FreeCamSettings.Folder, "state.json");
    static string BasePath => Path.Combine(FreeCamSettings.Folder, "base", File);
    static State LoadState()
    {
        try { if (System.IO.File.Exists(StatePath)) return JsonSerializer.Deserialize<State>(System.IO.File.ReadAllText(StatePath)) ?? new(); }
        catch (JsonException) { }
        return new();
    }
    static void SaveState(State s)
    {
        Directory.CreateDirectory(FreeCamSettings.Folder);
        string tmp = StatePath + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.Move(tmp, StatePath, overwrite: true);
    }

    public static string Live(string gameRoot) => Path.Combine(Settings.Cooked(gameRoot), File);
    public static string LoaderPath(string gameRoot) => Path.Combine(gameRoot, "UnrealEngine3", "Binaries", "Win64", "winmm.dll");
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    static readonly Dictionary<(string, long, DateTime), (Kind, string)> cache = [];

    /// <summary>What the live MarvelGame.upk is (read from its script, cached by size and write time) and whether Freecam's loader is there.</summary>
    public static Status Detect(string gameRoot)
    {
        string live = Live(gameRoot);
        bool loader = System.IO.File.Exists(LoaderPath(gameRoot));
        if (!System.IO.File.Exists(live)) return new(Kind.Missing, "", loader, false, "MarvelGame.upk isn't in the game folder.");
        var fi = new FileInfo(live);
        var key = (fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc);
        (Kind kind, string version) r;
        lock (cache)
            if (!cache.TryGetValue(key, out r)) cache[key] = r = Classify(System.IO.File.ReadAllBytes(live));
        bool ours = r.kind == Kind.Buddy && LoadState().WrittenSha is string w && w == Sha(System.IO.File.ReadAllBytes(live));
        string detail = r.kind switch
        {
            Kind.Stock => "The game's own camera: Freecam isn't installed.",
            Kind.Freecam => $"Freecam {r.version} is installed.",
            Kind.Buddy => ours ? "FreeCam Options is applied (on top of Freecam)." : "FreeCam Options's camera code is in the game, but not from this copy of the app.",
            _ => "MarvelGame.upk was changed by something else; FreeCam Options only works on top of Freecam.",
        };
        if (!loader && r.kind is Kind.Freecam or Kind.Buddy) detail += " Freecam's loader (winmm.dll) is missing: the game won't start with a changed MarvelGame.upk. Reinstall Freecam with its Full option.";
        return new(r.kind, r.version, loader, ours, detail);
    }

    /// <summary>Is the live MarvelGame.upk Freecam's or FreeCam Options's (the Changed Game Files check leaves those alone)?</summary>
    public static bool IsCameraMod(string gameRoot)
    {
        try { return Detect(gameRoot).Kind is Kind.Freecam or Kind.Buddy; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return false; }
    }

    static (Kind, string) Classify(byte[] bytes)
    {
        Package pkg;
        try { pkg = Package.FromBytes(bytes); } catch (PackageFormatException) { return (Kind.Other, ""); }
        try
        {
            var (_, _, zoom) = FreeCamPatch.Load(pkg, "MarvelPlayerController.AdjustCameraZoom");
            bool Refs(List<Node> code, string path)
            {
                int target; try { target = FreeCamPatch.Find(pkg, path).Item1 + 1; } catch (FreeCamKeyError) { return false; }
                bool hit = false;
                foreach (var s in code) Bytecode.Walk(s, n => { foreach (var o in n.Ops) if (o is OObj ob && ob.V == target) hit = true; });
                return hit;
            }
            bool stick = Refs(zoom, "MarvelInput.GamepadRawJoyLookUp");
            if (!stick) return (zoom.Count <= 6 ? Kind.Stock : Kind.Other, "");
            if (Refs(zoom, "MarvelPlayerController.Agent")) return (Kind.Buddy, "");
            if (zoom.Count != 10) return (Kind.Other, "");
            var (_, _, f11) = FreeCamPatch.Load(pkg, "MarvelPlayerController.CreateLootDummyShortcutDEBUG");
            bool v12 = f11.Any(s => { bool h = false; Bytecode.Walk(s, n => { if (n.Tok == 0x1B && n.Ops[0] is OName nm && pkg.Names[nm.Index].Equals("OcclusionOverrideToggle", StringComparison.OrdinalIgnoreCase)) h = true; }); return h; });
            return (Kind.Freecam, v12 ? "1.2" : "1.0 / 1.1");
        }
        catch (Exception ex) when (ex is InvalidDataException or FreeCamKeyError or IndexOutOfRangeException or ArgumentOutOfRangeException) { return (Kind.Other, ""); }
    }

    /// <summary>Builds and writes FreeCam Options with <paramref name="cfg"/>, then the key bindings. False with the reason in the log.</summary>
    /// <param name="modsNamingIt">Enabled mods that replace MarvelGame.upk, with their copy of it. Fine when the copy is a
    /// Freecam package (Kurt's "Free Cam with Walls, Ceilings and Skies" carries Freecam's file): FreeCam Options builds on top
    /// and the Mods tab's Apply leaves it there (KeepsOver). Any other copy: refused.</param>
    public static bool Apply(string gameRoot, FreeCamSettings cfg, IEnumerable<(string Name, string Copy)> modsNamingIt, bool dryRun, List<string> log, bool checkGameRunning = true)
    {
        if (!dryRun && checkGameRunning && System.Diagnostics.Process.GetProcessesByName("MarvelHeroesOmega").Length > 0) { log.Add("Close the game first."); return false; }
        foreach (var (name, copy) in modsNamingIt)
            if (!System.IO.File.Exists(copy) || Classify(System.IO.File.ReadAllBytes(copy)).Item1 != Kind.Freecam)
            { log.Add($"The mod \"{name}\" replaces MarvelGame.upk with a file that isn't Freecam's; FreeCam Options can't change it as well. Turn that mod off, or apply FreeCam Options without it."); return false; }
        var st = Detect(gameRoot);
        if (!st.CanApply) { log.Add(st.Detail); return false; }
        string live = Live(gameRoot);
        byte[] liveBytes = System.IO.File.ReadAllBytes(live);
        var state = LoadState();
        byte[] baseBytes;
        if (st.Kind == Kind.Freecam) baseBytes = liveBytes;
        else
        {
            if (!System.IO.File.Exists(BasePath)) { log.Add("The Freecam package FreeCam Options started from is missing from the data folder; reinstall Freecam, then apply again."); return false; }
            baseBytes = System.IO.File.ReadAllBytes(BasePath);
            if (Sha(baseBytes) != state.BaseSha) { log.Add("The kept Freecam package doesn't match its record; reinstall Freecam, then apply again."); return false; }
        }
        byte[] built;
        try { built = FreeCamPatch.BuildPackage(baseBytes, cfg, log); }
        catch (Exception ex) when (ex is InvalidDataException or FreeCamKeyError or PackageFormatException) { log.Add(ex.Message); return false; }
        string builtSha = Sha(built);
        bool same = builtSha == Sha(liveBytes);
        log.Add(same ? "MarvelGame.upk is already as these settings make it." : $"MarvelGame.upk: built and checked ({built.Length:N0} bytes).");
        if (dryRun) { HudLabels.Apply(gameRoot, cfg, dryRun: true, log); BindingsApply(cfg, dryRun: true, log); return true; }

        if (st.Kind == Kind.Freecam)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BasePath)!);
            string tmp = BasePath + ".tmp";
            System.IO.File.WriteAllBytes(tmp, baseBytes);
            if (Sha(System.IO.File.ReadAllBytes(tmp)) != Sha(baseBytes)) { log.Add("The copy of the Freecam package didn't read back identical; nothing written."); return false; }
            System.IO.File.Move(tmp, BasePath, overwrite: true);
            state.BaseSha = Sha(baseBytes);
            state.GameRoot = gameRoot;
            SaveState(state);
        }
        if (!same)
        {
            History.Label = "Ext Mod Manager: FreeCam Options";
            bool ok = MeshImport.WriteLive(live, built, onDisk =>
            {
                var p = new List<string>();
                if (Sha(onDisk) != builtSha) p.Add("bytes on disk differ from what was built");
                if (onDisk.Length < 4 || BitConverter.ToUInt32(onDisk, 0) != 0x9E2A83C1) p.Add("not a package (bad magic)");
                return p;
            }, bakBeside: false);
            if (!ok) { log.Add("Writing MarvelGame.upk failed; the game's file wasn't changed."); return false; }
        }
        state.WrittenSha = builtSha;
        SaveState(state);
        if (!HudLabels.Apply(gameRoot, cfg, dryRun: false, log)) return false;
        return BindingsApply(cfg, dryRun: false, log);
    }

    /// <summary>Puts the Freecam package back and takes the key bindings out.</summary>
    public static bool Remove(string gameRoot, bool dryRun, List<string> log, bool checkGameRunning = true)
    {
        if (!dryRun && checkGameRunning && System.Diagnostics.Process.GetProcessesByName("MarvelHeroesOmega").Length > 0) { log.Add("Close the game first."); return false; }
        var st = Detect(gameRoot);
        var state = LoadState();
        if (st.Kind == Kind.Buddy)
        {
            if (!st.Ours) { log.Add("FreeCam Options's code in the game wasn't written by this copy of the app; reinstall Freecam to remove it."); return false; }
            if (!System.IO.File.Exists(BasePath)) { log.Add("The Freecam package to put back is missing; reinstall Freecam instead."); return false; }
            byte[] baseBytes = System.IO.File.ReadAllBytes(BasePath);
            if (Sha(baseBytes) != state.BaseSha) { log.Add("The kept Freecam package doesn't match its record; reinstall Freecam instead."); return false; }
            log.Add("MarvelGame.upk: Freecam's own package goes back.");
            if (!dryRun)
            {
                History.Label = "Ext Mod Manager: FreeCam Options removed";
                string want = Sha(baseBytes);
                if (!MeshImport.WriteLive(Live(gameRoot), baseBytes, onDisk => Sha(onDisk) == want ? [] : ["bytes on disk differ from the Freecam package"], bakBeside: false))
                { log.Add("Writing MarvelGame.upk failed; the game's file wasn't changed."); return false; }
                state.WrittenSha = null;
                SaveState(state);
            }
        }
        else log.Add("MarvelGame.upk: nothing of FreeCam Options's to take out.");
        if (!HudLabels.Remove(gameRoot, dryRun, log)) return false;
        return BindingsRemove(dryRun, log);
    }

    /// <summary>
    /// For the Mods tab's Apply: a mod's MarvelGame.upk that FreeCam Options is built on. True while the live file is what
    /// FreeCam Options last wrote and <paramref name="source"/> is the Freecam package it started from, so Apply leaves the
    /// live file as it is; when the mod's copy changes, Apply writes it as usual (and FreeCam Options can be applied again).
    /// </summary>
    public static bool KeepsOver(string gameRoot, string file, string? source)
    {
        if (source == null || !file.Equals(File, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(source)) return false;
        var state = LoadState();
        string live = Live(gameRoot);
        if (state.WrittenSha == null || state.BaseSha == null || !System.IO.File.Exists(live)) return false;
        try { return Sha(System.IO.File.ReadAllBytes(live)) == state.WrittenSha && Sha(System.IO.File.ReadAllBytes(source)) == state.BaseSha; }
        catch (IOException) { return false; }
    }

    // ------------------------------------------------------------------ key bindings

    public static readonly (string Name, string Text)[] Keys =
    [
        ("", "None"), ("F1", "F1"), ("F2", "F2"), ("F3", "F3"), ("F4", "F4"), ("F5", "F5"), ("F6", "F6"), ("F7", "F7"), ("F8", "F8"),
        ("F9", "F9"), ("F10", "F10"), ("F11", "F11"), ("F12", "F12"), ("Insert", "Insert"), ("Home", "Home"), ("End", "End"),
        ("Delete", "Delete"), ("PageUp", "Page Up"), ("PageDown", "Page Down"),
        ("NumPadZero", "Num 0"), ("NumPadOne", "Num 1"), ("NumPadTwo", "Num 2"), ("NumPadThree", "Num 3"), ("NumPadFour", "Num 4"),
        ("NumPadFive", "Num 5"), ("NumPadSix", "Num 6"), ("NumPadSeven", "Num 7"), ("NumPadEight", "Num 8"), ("NumPadNine", "Num 9"),
        ("Z", "Z"), ("X", "X"), ("C", "C"), ("V", "V"), ("B", "B"), ("N", "N"), ("M", "M"), ("Y", "Y"), ("U", "U"), ("O", "O"), ("P", "P"), ("J", "J"), ("K", "K"),
    ];

    public static readonly (string Name, string Text)[] PadButtons =
    [
        ("", "None"), ("XboxTypeS_DPad_Up", "D-Pad Up"), ("XboxTypeS_DPad_Down", "D-Pad Down"), ("XboxTypeS_DPad_Left", "D-Pad Left"),
        ("XboxTypeS_DPad_Right", "D-Pad Right"), ("XboxTypeS_Back", "Back / View"), ("XboxTypeS_Start", "Start / Menu"),
        ("XboxTypeS_LeftShoulder", "LB"), ("XboxTypeS_RightShoulder", "RB"), ("XboxTypeS_LeftThumbStick", "L3 (Left Stick Click)"),
        ("XboxTypeS_RightThumbStick", "R3 (Right Stick Click)"), ("XboxTypeS_A", "A"), ("XboxTypeS_B", "B"), ("XboxTypeS_X", "X"), ("XboxTypeS_Y", "Y"),
    ];

    public readonly record struct Bind(string Name, bool Ctrl, bool Shift, bool Alt, bool Pad)
    {
        public static Bind? Parse(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var parts = spec.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            string name = parts[^1];
            bool pad = name.StartsWith("XboxTypeS_", StringComparison.OrdinalIgnoreCase);
            bool has(string m) => parts[..^1].Contains(m, StringComparer.OrdinalIgnoreCase);
            return new Bind(name, !pad && has("Ctrl"), pad ? has("RT") : has("Shift"), !pad && has("Alt"), pad);
        }

        /// <summary>A binding line for the input .ini, in the game's own form (exact modifiers; RT is the gamepad's Shift).</summary>
        public string Line(string prefix, string command) => Pad
            ? $"{prefix}Bindings=(Name=\"{Name}\",Command=\"{command}\",Control=False,Shift={B(Shift)},Alt=False,bIgnoreCtrl=True,bIgnoreShift={B(!Shift)},bIgnoreAlt=False)"
            : $"{prefix}Bindings=(Name=\"{Name}\",Command=\"{command}\",Control={B(Ctrl)},Shift={B(Shift)},Alt={B(Alt)},bIgnoreCtrl={B(!Ctrl)},bIgnoreShift={B(!Shift)},bIgnoreAlt={B(!Alt)})";
        static string B(bool v) => v ? "True" : "False";
    }

    static readonly string[] OurCommands = [FreeCamPatch.CycleMode, FreeCamPatch.PadButton, FreeCamPatch.PadButton2, FreeCamPatch.PadButton3, FreeCamPatch.SnapBehind, FreeCamPatch.Selfie,
        FreeCamPatch.OrbitCmd, FreeCamPatch.BehindCmd, FreeCamPatch.IsoCmd];
    /// <summary>MHO Camera 0.1.0's lines (native profiling commands), taken out wherever found.</summary>
    static readonly string[] Retired = ["DebugToggleGameProfiling", "DebugSavePerfFrameData", "DebugToggleStatRecording", "DebugStopTimedStatRecording"];
    static readonly string[] Files = ["MarvelInput.ini", "MarvelInputUserSettings.ini"];

    public static string InputFolder => Environment.GetEnvironmentVariable("MHO_FREECAM_INPUT_DIR") is { Length: > 0 } t ? t
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Marvel Heroes", "MarvelGame", "Config");

    static bool Ours(string line) => OurCommands.Concat(Retired).Any(c => line.Contains($"Command=\"{c}\"", StringComparison.OrdinalIgnoreCase) || line.Contains($"Command=\"{c} |", StringComparison.OrdinalIgnoreCase));

    /// <summary>What a key / button does now, without FreeCam Options's lines (the game's rule: the last matching binding wins).</summary>
    public static string? CurrentCommand(string spec)
    {
        if (Bind.Parse(spec) is not Bind b) return null;
        string path = Path.Combine(InputFolder, "MarvelInput.ini");
        if (!System.IO.File.Exists(path)) return null;
        string? found = null;
        string section = "";
        foreach (var raw in System.IO.File.ReadLines(path, Encoding.Latin1))
        {
            string l = raw.Trim();
            if (l.StartsWith('[')) { section = l; continue; }
            if (section != "[MarvelGame.MarvelInput]" || !l.StartsWith("Bindings=") || Ours(l)) continue;
            if (!l.Contains($"(Name=\"{b.Name}\"", StringComparison.OrdinalIgnoreCase)) continue;
            bool F(string field) => l.Contains($"{field}=True");
            bool ctrlOk = (!F("Control") || b.Ctrl) && (!F("bIgnoreCtrl") || !b.Ctrl);
            bool shiftOk = (!F("Shift") || b.Shift) && (!F("bIgnoreShift") || !b.Shift);
            bool altOk = (!F("Alt") || b.Alt) && (!F("bIgnoreAlt") || !b.Alt);
            if (!(ctrlOk && shiftOk && altOk)) continue;
            int c = l.IndexOf("Command=", StringComparison.Ordinal) + 8;
            string cmd = l[c..];
            cmd = cmd.StartsWith('"') ? cmd[1..cmd.IndexOf('"', 1)] : cmd[..Math.Max(0, cmd.IndexOf(','))];
            found = cmd;
        }
        return string.IsNullOrWhiteSpace(found) ? null : found;
    }

    static bool BindingsApply(FreeCamSettings cfg, bool dryRun, List<string> log)
    {
        var lines = new List<(Bind B, string Command)>();
        foreach (var (spec, cmd) in new[] { (cfg.ModeKey, FreeCamPatch.CycleMode), (cfg.SnapKey, FreeCamPatch.SnapBehind), (cfg.SelfieKey, FreeCamPatch.Selfie),
            (cfg.BehindKey, FreeCamPatch.BehindCmd), (cfg.IsoKey, FreeCamPatch.IsoCmd) })
            if (Bind.Parse(spec) is Bind b) lines.Add((b, cmd));
        // Gamepad buttons: one command on press and release per button in use (its tracker's), in FreeCamSettings.PadGroups
        // order; the game's script tells short, long and double apart and does that button's action for each.
        var groups = cfg.PadGroups();
        for (int i = 0; i < groups.Count && i < FreeCamPatch.Trackers.Length; i++)
            if (Bind.Parse(groups[i].Pad) is Bind pad)
                lines.Add((pad, $"{FreeCamPatch.Trackers[i].Exec} | OnRelease {FreeCamPatch.Trackers[i].Exec}"));
        return Rewrite(lines, dryRun, log);
    }

    static bool BindingsRemove(bool dryRun, List<string> log) => Rewrite([], dryRun, log);

    /// <summary>Takes FreeCam Options's lines out of both input files and appends <paramref name="add"/> at the end of the
    /// [MarvelGame.MarvelInput] bindings (the last match wins, so they override; taking them out brings the old ones back).
    /// Each file is backed up once (&lt;file&gt;.before-freecamoptions.bak) and written through a temp file.</summary>
    static bool Rewrite(List<(Bind B, string Command)> add, bool dryRun, List<string> log)
    {
        string dir = InputFolder;
        foreach (var name in Files)
        {
            string path = Path.Combine(dir, name);
            if (!System.IO.File.Exists(path)) { log.Add($"{name}: not found (start the game once so it makes its settings), key bindings skipped."); continue; }
            byte[] raw = System.IO.File.ReadAllBytes(path);
            if (raw.Length >= 2 && (raw[0] == 0xFF || raw[0] == 0xFE)) { log.Add($"{name}: UTF-16, not changed."); continue; }
            string text = Encoding.Latin1.GetString(raw);
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(nl).ToList();
            lines.RemoveAll(Ours);
            if (add.Count > 0)
            {
                bool user = name.Contains("UserSettings", StringComparison.OrdinalIgnoreCase);
                string section = ""; int at = -1, sectionStart = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string l = lines[i].Trim();
                    if (l.StartsWith('[')) { section = l; if (l == "[MarvelGame.MarvelInput]") sectionStart = i; continue; }
                    if (section == "[MarvelGame.MarvelInput]" && (user ? l.StartsWith("+Bindings=") : l.StartsWith("Bindings="))) at = i;
                }
                if (at < 0) at = sectionStart;
                if (at < 0) { log.Add($"{name}: no [MarvelGame.MarvelInput] section; key bindings skipped."); continue; }
                lines.InsertRange(at + 1, add.Select(x => x.B.Line(user ? "+" : "", x.Command)));
            }
            string result = string.Join(nl, lines);
            if (result == text) { log.Add($"{name}: key bindings already as wanted."); continue; }
            log.Add($"{name}: key bindings {(add.Count == 0 ? "taken out" : "written")}.");
            if (dryRun) continue;
            string bak = path + ".before-freecamoptions.bak";
            if (!System.IO.File.Exists(bak)) { System.IO.File.Copy(path, bak); if (!System.IO.File.ReadAllBytes(bak).AsSpan().SequenceEqual(raw)) { log.Add($"{name}: backup didn't read back; not changed."); return false; } }
            string tmp = path + ".freecamoptions.tmp";
            byte[] outBytes = Encoding.Latin1.GetBytes(result);
            System.IO.File.WriteAllBytes(tmp, outBytes);
            if (!System.IO.File.ReadAllBytes(tmp).AsSpan().SequenceEqual(outBytes)) { log.Add($"{name}: temp file didn't read back; not changed."); return false; }
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        return true;
    }
}
