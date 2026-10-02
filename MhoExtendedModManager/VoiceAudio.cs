using System.Media;
using NVorbis;

namespace MhoExtendedModManager;

/// <summary>
/// Plays a voice line's sound event (Editor → Voice tab). The event's audio is found the way the game finds it: the mod's
/// own sound pack first (its lines are events the pack adds, named after the package's AkEvents), else the bank holding the
/// event (ID = FNV of its name) in the game's .pck files: event → action → sound → media, embedded in the bank or streamed
/// from a .pck's stream table. Bank IDs aren't the hash of the AkBank's name (checked: magikvo_player's hash is in no .pck),
/// so banks are searched for the event, .pck files named after the event's group first (SFX_Magik_INT for magikvo_player),
/// and every event seen is remembered. Wwise Vorbis → Ogg (WwiseVorbis)
/// → PCM (NVorbis) → a WAV played by Windows. Reads only; nothing is written anywhere.
/// </summary>
static class VoiceAudio
{
    sealed record Where(string Pck, Akpk.Entry Entry);

    static readonly object gate = new();
    static string? indexed;
    static Dictionary<uint, Where> streams = [];
    static readonly Dictionary<uint, Where> events = [];
    static readonly HashSet<string> scanned = new(StringComparer.OrdinalIgnoreCase);
    static string[] pcks = [];
    static readonly Dictionary<string, (DateTime Time, SoundPack Pack)> packs = new(StringComparer.OrdinalIgnoreCase);
    static SoundPlayer? player;
    static MemoryStream? playing;

    static int Rank(string f)
    {
        string n = Path.GetFileNameWithoutExtension(f).ToUpperInvariant();
        return n.EndsWith("_INT") ? 0 : System.Text.RegularExpressions.Regex.IsMatch(n, "_[A-Z]{3}$") ? 2 : 1;
    }

    /// <summary>"SFX_Magik_INT.pck" → "magik" (what an event group like magikvo_player starts with).</summary>
    static string Key(string f)
    {
        string n = Path.GetFileNameWithoutExtension(f);
        if (n.StartsWith("SFX_", StringComparison.OrdinalIgnoreCase)) n = n[4..];
        return System.Text.RegularExpressions.Regex.Replace(n, "_[A-Za-z]{3}$", "").ToLowerInvariant();
    }

    /// <summary>The bank holding an event: hero-named .pck files first, then the rest; each .pck's banks are read once.</summary>
    static Where? FindEvent(uint id, string group)
    {
        lock (gate)
        {
            if (events.TryGetValue(id, out var w)) return w;
            string g = group.ToLowerInvariant();
            foreach (string pck in pcks.OrderBy(p => Key(p).Length >= 3 && g.StartsWith(Key(p)) ? 0 : 1).ThenBy(Rank))
            {
                if (!scanned.Add(pck)) continue;
                try
                {
                    using var f = File.OpenRead(pck);
                    var pk = Akpk.Read(f);
                    foreach (var e in pk.Banks)
                        foreach (uint ev in Akpk.EventIds(Akpk.ReadData(f, e))) events.TryAdd(ev, new Where(pck, e));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException) { }
                if (events.TryGetValue(id, out w)) return w;
            }
            return null;
        }
    }

    /// <summary>Stream tables of every .pck (headers only). English (_INT) and language-free files win. Read again when any
    /// .pck changes: Apply rebuilds them, and a stale index read banks at their old offsets (a crash on ▶ after applying a
    /// voice shift, 2026-10-02).</summary>
    static void Index(string cooked)
    {
        lock (gate)
        {
            var files = Directory.EnumerateFiles(cooked, "*.pck").OrderBy(Rank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            string stamp = cooked + "|" + string.Join("|", files.Select(f => { var i = new FileInfo(f); return $"{i.Name}:{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }));
            if (indexed == stamp) return;
            var s = new Dictionary<uint, Where>();
            pcks = files;
            foreach (string pck in pcks)
            {
                try
                {
                    using var f = File.OpenRead(pck);
                    foreach (var e in Akpk.Read(f).Streams) s.TryAdd(e.Id, new Where(pck, e));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException) { }
            }
            streams = s; events.Clear(); scanned.Clear(); indexed = stamp;
        }
    }

    static SoundPack Pack(string file)
    {
        var t = File.GetLastWriteTimeUtc(file);
        lock (gate)
        {
            if (packs.TryGetValue(file, out var c) && c.Time == t) return c.Pack;
            var p = SoundPack.Load(file);
            packs[file] = (t, p);
            return p;
        }
    }

    /// <summary>The .wem an event plays, and where it came from; throws InvalidDataException with a plain reason.</summary>
    public static (byte[] Wem, string From) Wem(string eventPath, IEnumerable<string> soundPacks, string cooked)
    {
        string leaf = eventPath[(eventPath.LastIndexOf('.') + 1)..];
        foreach (string file in soundPacks.Where(File.Exists))
        {
            var pack = Pack(file);
            if (pack.Patches.FirstOrDefault(p => p.EventName.Equals(leaf, StringComparison.OrdinalIgnoreCase)) is { } patch
                && pack.Wems.TryGetValue(patch.WemFile, out var wem))
                return (wem, $"{Path.GetFileName(file)}: {patch.WemFile}");
        }
        if (!Directory.Exists(cooked)) throw new InvalidDataException("the game folder isn't set");
        Index(cooked);
        string group = eventPath.Split('.')[0];
        if (FindEvent(SoundPack.Fnv(leaf), group) is not { } where)
            throw new InvalidDataException($"the event {leaf} isn't in the game's sound files");
        string bankName = $"{where.Entry.Id:X8}";
        (uint SourceId, byte[]? Embedded)? media;
        using (var f = File.OpenRead(where.Pck)) media = Akpk.EventMedia(f, where.Entry, SoundPack.Fnv(leaf));
        if (media is not { } m) throw new InvalidDataException($"the event {leaf} plays no sound");
        if (m.Embedded != null) return (m.Embedded, $"{Path.GetFileName(where.Pck)}: bank {bankName}, sound {m.SourceId}");
        if (!streams.TryGetValue(m.SourceId, out var st)) throw new InvalidDataException($"the sound {m.SourceId} isn't in the game's .pck files");
        using (var f = File.OpenRead(st.Pck)) return (Akpk.ReadData(f, st.Entry), $"{Path.GetFileName(st.Pck)}: sound {m.SourceId}");
    }

    /// <summary>Wwise Vorbis .wem → a 16-bit PCM WAV file in memory.</summary>
    public static byte[] ToWav(byte[] wem)
    {
        byte[] ogg = WwiseVorbis.ToOgg(wem, positions: true);
        using var reader = new VorbisReader(new MemoryStream(ogg), true);
        int ch = reader.Channels, rate = reader.SampleRate;
        var pcm = new MemoryStream();
        var buf = new float[rate * ch];
        int n;
        while ((n = reader.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++)
            {
                short v = (short)Math.Clamp((int)MathF.Round(buf[i] * 32767f), short.MinValue, short.MaxValue);
                pcm.WriteByte((byte)v); pcm.WriteByte((byte)(v >> 8));
            }
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write("RIFF"u8); w.Write((int)(36 + pcm.Length)); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)ch); w.Write(rate); w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write((int)pcm.Length); pcm.Position = 0; pcm.CopyTo(wav);
        return wav.ToArray();
    }

    /// <summary>Plays a WAV (stops whatever was playing).</summary>
    public static void Play(byte[] wav)
    {
        Stop();
        playing = new MemoryStream(wav);
        player = new SoundPlayer(playing);
        player.Play();
    }

    public static void Stop()
    {
        player?.Stop(); player?.Dispose(); player = null;
        playing?.Dispose(); playing = null;
    }
}
