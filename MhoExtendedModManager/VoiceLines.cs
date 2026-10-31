using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using NVorbis;

namespace MhoExtendedModManager;

/// <summary>
/// A voice's lines as files and back (Kurt, 2026-10-28: accents, translations, one line fixed; any outside tool or a microphone):
/// Export Lines writes each line as &lt;event&gt;.wav with lines.csv (what each one is); Replace Lines takes files named like
/// that. The audio goes in through VoiceShiftBuild, so the mod stays installable in MHModManager 1.0.1.
/// </summary>
static class VoiceLines
{
    /// <summary>The game's voice format (every voice in the game; see VoiceShiftBuild).</summary>
    public const int Rate = 22050;

    /// <summary>What Replace Lines and Replace Sounds read. .wav and .ogg by our own code; the others by Windows' own decoders
    /// (Media Foundation, through NAudio; Kurt, 2026-10-31).</summary>
    public static readonly string[] Extensions = [".wav", ".mp3", ".ogg", ".flac", ".m4a", ".wma"];

    public const string Formats = ".WAV, .MP3, .OGG, .FLAC, .M4A or .WMA";

    public static string FileFilter => "Audio (" + string.Join(";", Extensions.Select(e => "*" + e)) + ")|" + string.Join(";", Extensions.Select(e => "*" + e));

    public static string Leaf(string eventPath)
    {
        string path = VoiceShiftBuild.Original(eventPath);
        return path[(path.LastIndexOf('.') + 1)..];
    }

    /// <summary>The lines with sound as WAV files in <paramref name="folder"/>, as the mod plays them, and lines.csv. Returns the
    /// number written and the ones that have no sound.</summary>
    public static (int Written, List<string> Failed) Export(IEnumerable<VoiceLine> lines, IReadOnlyList<string> soundPacks, string cooked, string folder, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(folder);
        var list = lines.Where(l => l.Sound && !l.Missing).DistinctBy(l => Leaf(l.Event), StringComparer.OrdinalIgnoreCase).ToList();
        var csv = new StringBuilder("File,Situation,Detail,What's Said,Sound Event,Package\r\n");
        var failed = new List<string>();
        int written = 0;
        foreach (var l in list)
        {
            string leaf = Leaf(l.Event);
            progress?.Report($"Exporting Line {written + failed.Count + 1} of {list.Count}");
            try { File.WriteAllBytes(Path.Combine(folder, leaf + ".wav"), VoiceAudio.ToWav(VoiceAudio.Wem(l.Event, soundPacks, cooked).Wem)); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IndexOutOfRangeException) { failed.Add($"{leaf}: {ex.Message}"); continue; }
            written++;
            csv.Append(string.Join(",", new[] { leaf + ".wav", l.Situation, l.Detail, VoiceText.For(l.Event), VoiceShiftBuild.Original(l.Event), l.Package }.Select(Quote))).Append("\r\n");
        }
        File.WriteAllText(Path.Combine(folder, "lines.csv"), csv.ToString(), new UTF8Encoding(true));
        return (written, failed);
    }

    static string Quote(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>The audio files in a folder that are named after one of the lines (line event name → file), and the others.</summary>
    public static (Dictionary<string, string> Matched, List<string> Unknown) Match(string folder, IEnumerable<string> leaves)
    {
        var known = leaves.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (string f in Directory.EnumerateFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase))
            if (known.Contains(Path.GetFileNameWithoutExtension(f))) matched[Path.GetFileNameWithoutExtension(f)] = f;
            else unknown.Add(Path.GetFileName(f));
        return (matched, unknown);
    }

    /// <summary>An audio file (see Extensions; a .wav can be 8, 16, 24 or 32 bit, or float; any rate; mono or stereo) as a 16-bit
    /// mono WAV at the game's rate. Throws InvalidDataException with a plain reason.</summary>
    public static byte[] Load(string file)
    {
        var (samples, rate) = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".wav" => ReadWav(File.ReadAllBytes(file)),
            ".ogg" => ReadOgg(file),
            _ => ReadWindows(file),
        };
        if (samples.Length < rate / 20) throw new InvalidDataException("it's under 0.05 seconds long");
        if (rate != Rate) samples = VoiceShift.Resample(samples, rate / (float)Rate);
        return Wav(samples);
    }

    static (float[], int) ReadOgg(string file)
    {
        using var r = new VorbisReader(file);
        return (Mono(r.ReadSamples, r.SampleRate, r.Channels), r.SampleRate);
    }

    /// <summary>MP3, FLAC, M4A, WMA: Windows decodes them. COMException = Windows has no decoder for it, or the file is damaged.</summary>
    static (float[], int) ReadWindows(string file)
    {
        try
        {
            using var r = new MediaFoundationReader(file);
            var samples = r.ToSampleProvider();
            return (Mono(samples.Read, samples.WaveFormat.SampleRate, samples.WaveFormat.Channels), samples.WaveFormat.SampleRate);
        }
        catch (COMException ex) { throw new InvalidDataException($"Windows can't read it (0x{ex.HResult:X8}): the file may be damaged, or this Windows has no decoder for it. Save it as .WAV"); }
    }

    static float[] Mono(Func<float[], int, int, int> read, int rate, int ch)
    {
        var all = new List<float>();
        var buf = new float[rate * ch];
        int n, left = 0;
        // (Whole frames only: a reader may stop mid-frame, and the rest then starts the next buffer.)
        while ((n = read(buf, left, buf.Length - left)) > 0)
        {
            n += left;
            int i = 0;
            for (; i + ch <= n; i += ch) { float s = 0; for (int k = 0; k < ch; k++) s += buf[i + k]; all.Add(s / ch); }
            left = n - i;
            Array.Copy(buf, i, buf, 0, left);
        }
        return [.. all];
    }

    static (float[], int) ReadWav(byte[] b)
    {
        if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") throw new InvalidDataException("it isn't a WAV file");
        int format = 0, ch = 0, rate = 0, bits = 0, data = -1, len = 0;
        for (int p = 12; p + 8 <= b.Length;)
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            int size = BitConverter.ToInt32(b, p + 4);
            if (size < 0) break;
            if (id == "fmt " && p + 24 <= b.Length)
            {
                format = BitConverter.ToUInt16(b, p + 8); ch = BitConverter.ToInt16(b, p + 10); rate = BitConverter.ToInt32(b, p + 12); bits = BitConverter.ToInt16(b, p + 22);
                if (format == 0xFFFE && p + 34 <= b.Length) format = BitConverter.ToUInt16(b, p + 32);   // (extensible: the real format)
            }
            else if (id == "data") { data = p + 8; len = Math.Min(size, b.Length - data); break; }
            p += 8 + size + (size & 1);
        }
        if (data < 0 || ch < 1 || rate < 1) throw new InvalidDataException("its WAV header doesn't read");
        if (format != 1 && format != 3 || format == 3 && bits != 32 && bits != 64 || format == 1 && bits is not (8 or 16 or 24 or 32))
            throw new InvalidDataException($"its WAV format isn't one this reads (format {format}, {bits} bit): save it as 16-bit PCM");
        int step = bits / 8, frames = len / step / ch;
        var o = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float s = 0;
            for (int k = 0; k < ch; k++)
            {
                int at = data + (i * ch + k) * step;
                s += format == 3 ? (bits == 32 ? BitConverter.ToSingle(b, at) : (float)BitConverter.ToDouble(b, at))
                    : bits switch
                    {
                        8 => (b[at] - 128) / 128f,
                        16 => BitConverter.ToInt16(b, at) / 32768f,
                        24 => (b[at] | b[at + 1] << 8 | (sbyte)b[at + 2] << 16) / 8388608f,
                        _ => BitConverter.ToInt32(b, at) / 2147483648f,
                    };
            }
            o[i] = s / ch;
        }
        return (o, rate);
    }

    static byte[] Wav(float[] samples)
    {
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        int bytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (float s in samples) w.Write((short)Math.Clamp((int)MathF.Round(s * 32767f), short.MinValue, short.MaxValue));
        return wav.ToArray();
    }
}
