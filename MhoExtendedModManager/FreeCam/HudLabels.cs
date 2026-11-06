using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager.FreeCam;

/// <summary>
/// FreeCam Options's Overhead Labels (0.60.420): hides the names / titles / guild names drawn over characters. They are the
/// Flash (AS2) class MarvelInWorldEntityInfo in MarvelHUD_SF.upk's SwfMovie "hud_manager" (an uncompressed GFX movie inside
/// the export). Its setters __set__name / __set__titleName / __set__guildName each do "xxx_tf.htmlText = val"
/// (Push c"htmlText", r2; SetMember); the game sets every label through them. Pushing the constant "" instead of r2 (same
/// size, nothing else moves) shows an empty label. Confirmed in game 2026-11-06 (names and titles, Telem test client).
/// Only client files: MarvelHUD_SF.upk is an existing game package, written through MeshImport.WriteLive like any mod's.
/// Apply builds from a kept copy of the package it started from (data\freecam\base\MarvelHUD_SF.upk: stock, or a mod's copy);
/// with every label shown again (or on Remove) that copy goes back byte for byte.
/// </summary>
static class HudLabels
{
    public const string File = "MarvelHUD_SF.upk";

    sealed class State
    {
        public string? BaseSha { get; set; }
        public string? WrittenSha { get; set; }
    }

    static string StatePath => Path.Combine(FreeCamSettings.Folder, "hud_state.json");
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
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    public static string Live(string gameRoot) => Path.Combine(Settings.Cooked(gameRoot), File);

    /// <summary>The setters in the order the options are listed.</summary>
    static readonly (string Setter, string What)[] Setters = [("__set__name", "names"), ("__set__titleName", "titles"), ("__set__guildName", "guild names")];

    /// <summary>The package with the chosen labels blanked (from an unchanged one). Throws InvalidDataException when the movie isn't as expected.</summary>
    public static byte[] Build(byte[] packageBytes, bool names, bool titles, bool guilds, List<string> log)
    {
        var pkg = Package.FromBytes(packageBytes);
        int idx = Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals("hud_manager", StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(e).Equals("SwfMovie", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) throw new InvalidDataException("MarvelHUD_SF.upk has no hud_manager movie.");
        byte[] data = pkg.ReadExportBytes(pkg.Exports[idx]);
        var hits = Find(data);
        bool[] want = [names, titles, guilds];
        for (int i = 0; i < 3; i++)
        {
            if (!want[i]) continue;
            var (at, empty) = hits[i];
            data[at + 5] = 0x08; data[at + 6] = (byte)empty;
            log.Add($"MarvelHUD_SF.upk: overhead {Setters[i].What} hidden.");
        }
        var replaced = new Dictionary<int, byte[]> { [idx] = data };
        byte[] output = FreeCamPackageEdit.ReplaceExports(pkg, replaced);
        var problems = FreeCamPackageEdit.Verify(pkg, Package.FromBytes(output), replaced);
        if (problems.Count > 0) throw new InvalidDataException("The built MarvelHUD_SF.upk didn't check out: " + string.Join("; ", problems));
        return output;
    }

    /// <summary>Each setter's "Push c"htmlText", r2" action (offset in the export) and the constant index of "".</summary>
    static (int At, int Empty)[] Find(byte[] d)
    {
        int gfx = IndexOf(d, "GFX"u8, 0);
        if (gfx < 0) throw new InvalidDataException("hud_manager: no uncompressed Flash movie inside.");
        // the class's DoInitAction tag: the one whose constant pool names the setters (ExportAssets names only the class)
        int inside = IndexOf(d, "__set__titleName\0"u8, gfx);
        var (start, end) = TagOf(d, gfx, inside);
        if (start < 0 || d[start] != 0x88) throw new InvalidDataException("hud_manager: the label code wasn't found.");
        int poolLen = BitConverter.ToUInt16(d, start + 1), count = BitConverter.ToUInt16(d, start + 3);
        var consts = Encoding.Latin1.GetString(d, start + 5, poolLen - 2).Split('\0').Take(count).ToList();
        int htmlText = consts.IndexOf("htmlText"), empty = consts.IndexOf("");
        if (htmlText is < 0 or > 255 || empty is < 0 or > 255) throw new InvalidDataException("hud_manager: the label code isn't as expected.");
        var setterIdx = Setters.Select(s => consts.IndexOf(s.Setter)).ToArray();
        if (setterIdx.Any(i => i < 0)) throw new InvalidDataException("hud_manager: a label setter is missing.");

        // Walk the actions: a Push naming a setter, then the next "Push c"htmlText", r2" (before SetMember) is its body's.
        var found = new int[3] { -1, -1, -1 };
        int current = -1;
        for (int p = start; p < end;)
        {
            byte op = d[p];
            if (op == 0) break;
            int len = op >= 0x80 ? BitConverter.ToUInt16(d, p + 1) : 0;
            int arg = p + 3, next = op >= 0x80 ? arg + len : p + 1;
            if (op == 0x96)
            {
                for (int r = arg; r < arg + len - 1; r++)
                    if (d[r] == 0x08 && Array.IndexOf(setterIdx, (int)d[r + 1]) is int k and >= 0) { current = k; break; }
                if (len == 4 && d[arg] == 0x08 && d[arg + 1] == htmlText && d[arg + 2] == 0x04 && d[arg + 3] == 0x02 && d[next] == 0x4F && current >= 0)
                {
                    if (found[current] >= 0) throw new InvalidDataException($"hud_manager: {Setters[current].Setter} sets its text twice.");
                    found[current] = p;
                }
            }
            p = next;
        }
        for (int i = 0; i < 3; i++)
            if (found[i] < 0) throw new InvalidDataException($"hud_manager: {Setters[i].Setter} isn't as expected (already changed by another mod?).");
        return found.Select(f => (f, empty)).ToArray();
    }

    /// <summary>The body (start, end) of the DoInitAction / DoAction tag holding <paramref name="inside"/>, after DoInitAction's sprite id.</summary>
    static (int, int) TagOf(byte[] d, int gfx, int inside)
    {
        if (inside < 0) return (-1, -1);
        int p = gfx + 8;
        int nbits = d[p] >> 3;
        p += (5 + 4 * nbits + 7) / 8 + 4;
        while (p + 2 <= d.Length)
        {
            int code = BitConverter.ToUInt16(d, p); p += 2;
            int tag = code >> 6, len = code & 0x3F;
            if (len == 0x3F) { len = BitConverter.ToInt32(d, p); p += 4; }
            if (tag == 0) break;
            if (inside >= p && inside < p + len)
                return tag == 59 ? (p + 2, p + len) : tag == 12 ? (p, p + len) : (-1, -1);
            p += len;
        }
        return (-1, -1);
    }

    static int IndexOf(byte[] d, ReadOnlySpan<byte> p, int from)
    {
        if (from < 0) return -1;
        int i = d.AsSpan(from).IndexOf(p);
        return i < 0 ? -1 : from + i;
    }

    /// <summary>Is the live MarvelHUD_SF.upk what FreeCam Options last wrote?</summary>
    public static bool IsOurs(string gameRoot)
    {
        var st = LoadState();
        string live = Live(gameRoot);
        try { return st.WrittenSha != null && System.IO.File.Exists(live) && Sha(System.IO.File.ReadAllBytes(live)) == st.WrittenSha; }
        catch (IOException) { return false; }
    }

    /// <summary>Writes MarvelHUD_SF.upk for the chosen labels (or puts the kept copy back when none is hidden). False with the reason in the log.</summary>
    public static bool Apply(string gameRoot, FreeCamSettings cfg, bool dryRun, List<string> log)
    {
        string live = Live(gameRoot);
        bool any = cfg.HideNames || cfg.HideTitles || cfg.HideGuilds;
        var state = LoadState();
        if (!System.IO.File.Exists(live)) { if (any) log.Add("MarvelHUD_SF.upk isn't in the game folder; labels left as they are."); return !any; }
        byte[] liveBytes = System.IO.File.ReadAllBytes(live);
        bool ours = state.WrittenSha != null && Sha(liveBytes) == state.WrittenSha;
        byte[] baseBytes;
        if (!ours) baseBytes = liveBytes;
        else
        {
            if (!System.IO.File.Exists(BasePath)) { log.Add("The kept MarvelHUD_SF.upk is missing from the data folder; labels left as they are."); return false; }
            baseBytes = System.IO.File.ReadAllBytes(BasePath);
            if (Sha(baseBytes) != state.BaseSha) { log.Add("The kept MarvelHUD_SF.upk doesn't match its record; labels left as they are."); return false; }
        }
        if (!any)
        {
            if (!ours) return true;
            log.Add("MarvelHUD_SF.upk: every overhead label shown again (the kept copy goes back).");
            if (dryRun) return true;
            if (!Write(live, baseBytes, "Ext Mod Manager: FreeCam Options labels shown")) { log.Add("Writing MarvelHUD_SF.upk failed; the game's file wasn't changed."); return false; }
            state.WrittenSha = null; SaveState(state);
            return true;
        }
        byte[] built;
        try { built = Build(baseBytes, cfg.HideNames, cfg.HideTitles, cfg.HideGuilds, log); }
        catch (Exception ex) when (ex is InvalidDataException or PackageFormatException) { log.Add(ex.Message + " Labels left as they are."); return false; }
        string builtSha = Sha(built);
        if (builtSha == Sha(liveBytes)) { log.Add("MarvelHUD_SF.upk is already as these settings make it."); return true; }
        if (dryRun) return true;
        if (!ours)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BasePath)!);
            string tmp = BasePath + ".tmp";
            System.IO.File.WriteAllBytes(tmp, baseBytes);
            if (Sha(System.IO.File.ReadAllBytes(tmp)) != Sha(baseBytes)) { log.Add("The copy of MarvelHUD_SF.upk didn't read back identical; nothing written."); return false; }
            System.IO.File.Move(tmp, BasePath, overwrite: true);
            state.BaseSha = Sha(baseBytes);
            SaveState(state);
        }
        if (!Write(live, built, "Ext Mod Manager: FreeCam Options labels")) { log.Add("Writing MarvelHUD_SF.upk failed; the game's file wasn't changed."); return false; }
        state.WrittenSha = builtSha; SaveState(state);
        return true;
    }

    /// <summary>Puts the kept MarvelHUD_SF.upk back when FreeCam Options wrote the live one.</summary>
    public static bool Remove(string gameRoot, bool dryRun, List<string> log)
    {
        var cfg = new FreeCamSettings { HideNames = false, HideTitles = false, HideGuilds = false };
        return Apply(gameRoot, cfg, dryRun, log);
    }

    static bool Write(string live, byte[] bytes, string label)
    {
        History.Label = label;
        string want = Sha(bytes);
        return MeshImport.WriteLive(live, bytes, onDisk =>
        {
            var p = new List<string>();
            if (Sha(onDisk) != want) p.Add("bytes on disk differ from what was built");
            if (onDisk.Length < 4 || BitConverter.ToUInt32(onDisk, 0) != 0x9E2A83C1) p.Add("not a package (bad magic)");
            return p;
        }, bakBeside: false);
    }

    /// <summary>For the Mods tab's Apply: a mod's MarvelHUD_SF.upk that the labels were built on: left as it is while the
    /// live file is what FreeCam Options last wrote and <paramref name="source"/> is the copy it started from.</summary>
    public static bool KeepsOver(string gameRoot, string file, string? source)
    {
        if (source == null || !file.Equals(File, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(source)) return false;
        var st = LoadState();
        if (st.BaseSha == null || !IsOurs(gameRoot)) return false;
        try { return Sha(System.IO.File.ReadAllBytes(source)) == st.BaseSha; }
        catch (IOException) { return false; }
    }

    /// <summary>For the Mods tab's Apply: MarvelHUD_SF.upk isn't a left-over to put back while it's FreeCam Options's.</summary>
    public static bool Owns(string gameRoot, string file) => file.Equals(File, StringComparison.OrdinalIgnoreCase) && IsOurs(gameRoot);
}
