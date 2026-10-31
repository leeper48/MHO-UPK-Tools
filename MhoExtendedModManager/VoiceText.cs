using System.IO.Compression;
using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// What a sound event says (Kurt, 2026-10-31: transcriptions on the Voice tab): StockData\voice_text.tsv.gz, sound event ID (the
/// hash of its name) → the text. Speech-to-text from MH_PCK_SoundSilencer by CorvaeOboro (CC0), so it's close, not exact;
/// keyed by event so a line's text needs no game file. Made by Assets\make_voice_text.py and --voice-text-make.
/// </summary>
static class VoiceText
{
    static readonly object gate = new();
    static Dictionary<uint, string>? table;

    static Dictionary<uint, string> Table()
    {
        lock (gate)
        {
            if (table != null) return table;
            var t = new Dictionary<uint, string>();
            string file = Path.Combine(AppContext.BaseDirectory, "StockData", "voice_text.tsv.gz");
            try
            {
                using var z = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
                using var r = new StreamReader(z, Encoding.UTF8);
                while (r.ReadLine() is string line)
                    if (line.IndexOf('\t') is int tab and > 0 && uint.TryParse(line.AsSpan(0, tab), System.Globalization.NumberStyles.HexNumber, null, out uint id)) t[id] = line[(tab + 1)..];
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }   // (no text: the column stays empty)
            return table = t;
        }
    }

    /// <summary>The text of an event (its path or name; a shifted copy's is its original's), or "".</summary>
    public static string For(string eventPath)
    {
        string path = VoiceShiftBuild.Original(eventPath);
        return Table().GetValueOrDefault(SoundPack.Fnv(path[(path.LastIndexOf('.') + 1)..]), "");
    }

    /// <summary>
    /// The table made: <paramref name="sounds"/> (make_voice_text.py: sound ID → text) through every event of the clean game's
    /// English and language-free .pck files. Reads the game's files only.
    /// </summary>
    public static (int Events, int Sounds) Make(string sounds, string cleanCooked, string outFile)
    {
        var text = new Dictionary<uint, string>();
        using (var z = new GZipStream(File.OpenRead(sounds), CompressionMode.Decompress))
        using (var r = new StreamReader(z, Encoding.UTF8))
            while (r.ReadLine() is string line)
                if (line.Split('\t', 2) is [var id, var said]) text[Convert.ToUInt32(id, 16)] = said;
        var events = new SortedDictionary<uint, string>();
        var used = new HashSet<uint>();
        foreach (string pck in Directory.EnumerateFiles(cleanCooked, "*.pck").Order(StringComparer.OrdinalIgnoreCase))
        {
            // (not the other languages' files: their banks hold the same events with other sounds)
            if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(pck), "_(?!INT$)[A-Z]{3}$")) continue;
            using var f = File.OpenRead(pck);
            foreach (var bank in Akpk.Read(f).Banks)
                foreach (var (ev, source) in Akpk.AllEventMedia(f, bank))
                    if (text.TryGetValue(source, out var said) && events.TryAdd(ev, said)) used.Add(source);
        }
        using (var z = new GZipStream(File.Create(outFile), CompressionLevel.SmallestSize))
        using (var w = new StreamWriter(z, new UTF8Encoding(false)))
            foreach (var (ev, said) in events) w.Write($"{ev:x8}\t{said}\n");
        return (events.Count, used.Count);
    }
}
