using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>A costume package's voice shift, kept in the manifest (VoiceShifts) so the editor shows and can redo it.</summary>
sealed class VoiceShiftEntry
{
    public string Package { get; set; } = "";
    public float Pitch { get; set; }
    public float Formant { get; set; }
    public float Warmth { get; set; }
}

/// <summary>
/// A costume's whole voice, pitch / formant shifted (Kurt, 2026-10-02: female to male and back), in a form MHModManager 1.0.1
/// applies too. Each line's event gets a copy in the package (…_mhoshift, same bank) and the voice set points at the copies;
/// a sound pack (.mhsfx, the old manager's format) adds those events to the game's banks, cloned from the originals, with the
/// shifted audio as Wwise Vorbis (WwiseVorbisWrite at q0.4: the setup every game voice uses). Lines that are off stay off.
/// Checked 2026-10-02: the pack applied by MHModManager's own sound library and by this app gives a byte-identical .pck;
/// the shifted lines play in game.
/// </summary>
static class VoiceShiftBuild
{
    public const string Suffix = "_mhoshift";

    /// <summary>A voice bank (speech, vocal efforts): wwisedefaultbank_cyclopsvo, …_cyclopsvoicefx, …vo_player, …vo_teamup.</summary>
    public static bool IsVoiceBank(string bank)
    {
        string n = bank.ToLowerInvariant().Replace("wwisedefaultbank_", "");
        return n.EndsWith("vo") || n.Contains("vo_") || n.Contains("voice");
    }

    /// <summary>The sound pack's file name for a package.</summary>
    public static string PackName(string packageFile) => "VoiceShift_" + Path.GetFileNameWithoutExtension(packageFile) + ".mhsfx";

    /// <summary>The original event of a shifted line's event (…_mhoshift), else the event itself.</summary>
    public static bool ExpectedSkip(string logLine) => logLine.Contains("(a sound effect") || logLine.Contains("(not a sound event");

    public static string Original(string eventPath) => eventPath.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? eventPath[..^Suffix.Length] : eventPath;

    /// <summary>
    /// The package with its voice set pointing back at the original events (an earlier shift undone); null when nothing was
    /// shifted.
    /// </summary>
    public static byte[]? Unshift(string packagePath, string packageFile, IReadOnlyList<VoiceOffEntry> off)
    {
        var changes = VoiceSet.Read(packageFile, packagePath, off).Where(l => !l.Off && l.Event.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            .Select(l => (l.Offset, (string?)Original(l.Event))).ToList();
        return changes.Count == 0 ? null : VoiceSet.Write(packagePath, changes);
    }

    /// <summary>
    /// Renders the shift. Returns the new package and the sound pack (both written into <paramref name="work"/>), and a log.
    /// <paramref name="soundPacks"/>: the mod's sound packs (other than an earlier shift pack), read for lines they voice.
    /// </summary>
    public static (string Package, string Pack, List<string> Log) Build(string packagePath, string packageFile, IReadOnlyList<VoiceOffEntry> off,
        IReadOnlyList<string> soundPacks, string cooked, VoiceShiftEntry s, string work, IProgress<string>? progress = null)
    {
        var log = new List<string>();
        Directory.CreateDirectory(work);
        // The package as it grows is kept in memory (a file per copied event was 1.1 GB for Sentry's 123 lines).
        string cur = packagePath;
        if (Unshift(cur, packageFile, off) is { } undone) { cur = Path.Combine(work, "unshift_" + packageFile); File.WriteAllBytes(cur, undone); }
        var pkg = Package.Open(cur);
        bool grown = false;
        var lines = VoiceSet.Read(packageFile, cur, off).Where(l => !l.Off && !l.Missing && l.Sound).ToList();
        if (lines.Count == 0) throw new InvalidDataException("this package's voice set has no lines that are on");
        var index = Akpk.EventIndex(cooked);
        var patches = new List<Dictionary<string, string>>();
        var wems = new Dictionary<string, byte[]>();
        var newPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // original event path → copy's path
        var events = lines.Select(l => l.Event).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int done = 0;
        foreach (string ev in events)
        {
            progress?.Report($"Shifting Line {++done} of {events.Count}");
            string leaf = ev[(ev.LastIndexOf('.') + 1)..], group = ev[..ev.LastIndexOf('.')], nleaf = leaf + Suffix;
            // The event's copy in the package, under the new name; its bank by the AkBank's name (the old manager's lookup).
            // A voice set can name things other than sounds (Storm's emotes point at an animation): left as they are.
            int ei = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(ev, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(e).Equals("AkEvent", StringComparison.OrdinalIgnoreCase));
            if (ei < 0) { log.Add($"{leaf}: left as it is (not a sound event)"); continue; }
            byte[] wem;
            try { wem = VoiceAudio.Wem(ev, soundPacks, cooked).Wem; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException) { log.Add($"{leaf}: left as it is ({ex.Message})"); continue; }
            if (!index.TryGetValue(SoundPack.Fnv(leaf), out var where)) { log.Add($"{leaf}: left as it is (its event isn't in the game's sound banks)"); continue; }
            byte[] ed = pkg.ReadExportBytes(pkg.Exports[ei]).ToArray();
            string? bank = TagWalker.Walk(pkg, ed, 4)?.FirstOrDefault(t => t.Name.Equals("RequiredBank", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } rb
                ? pkg.RefName(BinaryPrimitives.ReadInt32LittleEndian(ed.AsSpan(rb.ValueAt))) : null;
            if (bank == null || SoundPack.Fnv(bank) != where.Bank) { log.Add($"{leaf}: left as it is (its bank {bank ?? "?"} isn't the one it's in)"); continue; }
            // Voice only (Kurt, 2026-10-02): a voice set can also name sound effects (Sentry's has a dodge whoosh in
            // dodgepassivesfx); only lines in voice banks (…vo, …vo_…, …voice…: speech and vocal efforts) are shifted.
            if (!IsVoiceBank(bank)) { log.Add($"{leaf}: left as it is (a sound effect, in {bank})"); continue; }
            byte[] shifted = VoiceShift.Shift(VoiceAudio.ToWav(wem), s.Pitch, s.Formant, VoiceShift.Mode.Natural, s.Warmth);
            byte[] outWem = WwiseVorbisWrite.ToWem(VorbisEncode.Encode(shifted, 0.4f), WavPcm.Read(shifted).Channels[0].Length);
            if (!pkg.Exports.Any(e => e.ObjectName.Equals(nleaf, StringComparison.OrdinalIgnoreCase)))
            {
                var cp = CrossMove.Quiet(() => ExportCopy.Copy(pkg, ei, pkg, [], nleaf), out string said) ?? throw new InvalidDataException($"{leaf}: the event couldn't be copied ({said.Trim()})");
                pkg = Package.FromBytes(cp.Output);
                grown = true;
            }
            newPath[ev] = group + "." + nleaf;
            wems[nleaf + ".wem"] = outWem;
            patches.Add(new Dictionary<string, string>
            {
                ["type"] = "new_event", ["original_event_name"] = leaf, ["event_name"] = nleaf, ["event_hash"] = $"0x{SoundPack.Fnv(nleaf):X8}",
                ["action_id"] = $"0x{SoundPack.Fnv(nleaf + "_action"):X8}", ["sound_id"] = $"0x{SoundPack.Fnv(nleaf + "_sound"):X8}", ["source_id"] = $"0x{SoundPack.Fnv(nleaf + "_source"):X8}",
                ["wem_file"] = nleaf + ".wem", ["bank_name"] = bank, ["pck_file"] = where.Pck,
            });
        }
        if (patches.Count == 0) throw new InvalidDataException("no line could be shifted: " + string.Join("; ", log.Take(3)));
        // The voice set at the copies.
        var changes = lines.Where(l => newPath.ContainsKey(l.Event)).Select(l => (l.Offset, (string?)newPath[l.Event])).ToList();
        if (grown) { cur = Path.Combine(work, "events_" + packageFile); File.WriteAllBytes(cur, pkg.RawFile); }
        string outPkg = Path.Combine(work, packageFile);
        File.WriteAllBytes(outPkg, VoiceSet.Write(cur, changes) ?? throw new InvalidDataException("the voice set couldn't be written"));
        string pack = Path.Combine(work, PackName(packageFile));
        if (File.Exists(pack)) File.Delete(pack);
        using (var z = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            using (var sw = new StreamWriter(z.CreateEntry("mod.json").Open()))
                sw.Write(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["name"] = $"Voice shift: pitch {s.Pitch:+0.#;-0.#;0}, formant {s.Formant:+0.#;-0.#;0}, warmth {s.Warmth:+0.#;-0.#;0} dB",
                    ["patches"] = patches,
                }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var (n, b) in wems) using (var e = z.CreateEntry(n).Open()) e.Write(b);
        }
        log.Insert(0, $"{patches.Count} line(s) shifted ({lines.Count} voice set entries), {wems.Values.Sum(w => (long)w.Length) / 1024:N0} KB of audio");
        return (outPkg, pack, log);
    }
}
